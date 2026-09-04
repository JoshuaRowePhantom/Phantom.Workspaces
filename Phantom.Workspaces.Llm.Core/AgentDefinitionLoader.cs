using AgentSchema;
using System.Text.Json;
using Json.Schema;
using YamlDotNet.Core;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Loads and parses supported AgentDefinition content from JSON or YAML.
/// 
/// IMPORTANT: Keep <c>JsonSchemas\AgentDefinition.json</c> in sync with the entities and
/// fields this loader supports. Any expansion of supported AgentDefinition shape must include
/// corresponding schema updates to preserve validation behavior.
/// </summary>
public static class AgentDefinitionLoader
{
    /// <summary>
    /// Loads an agent definition from a JSON or YAML file.
    /// </summary>
    /// <param name="filePath">Path to the agent definition file (.json or .yaml/.yml).</param>
    /// <returns>The loaded agent definition.</returns>
    /// <exception cref="FileNotFoundException">If the file does not exist.</exception>
    /// <exception cref="InvalidOperationException">If the file format is not supported or parsing fails.</exception>
    public static AgentDefinition LoadAgent(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Agent definition file not found: {filePath}");
        }

        var content = File.ReadAllText(filePath);
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        return extension switch
        {
            ".json" => ParseJsonAgent(content, filePath),
            ".yaml" or ".yml" => ParseYamlAgent(content, filePath),
            _ => throw new InvalidOperationException(
                $"Unsupported file format: {extension}. Use .json, .yaml, or .yml files.")
        };
    }

    /// <summary>
    /// Loads an agent definition from a JSON string.
    /// </summary>
    /// <param name="json">The JSON content.</param>
    /// <returns>The loaded agent definition.</returns>
    /// <exception cref="InvalidOperationException">If parsing or schema validation fails.</exception>
    public static AgentDefinition LoadAgentFromJson(string json)
    {
        return ParseJsonAgent(json, "<json-content>");
    }

    /// <summary>
    /// Loads an agent definition from a YAML string.
    /// </summary>
    /// <param name="yaml">The YAML content.</param>
    /// <returns>The loaded agent definition.</returns>
    /// <exception cref="InvalidOperationException">If parsing fails.</exception>
    public static AgentDefinition LoadAgentFromYaml(string yaml)
    {
        return ParseYamlAgent(yaml, "<yaml-content>");
    }

    private static AgentDefinition ParseJsonAgent(string content, string sourceLabel)
    {
        try
        {
            ValidateJsonAgainstSchema(content, sourceLabel);

            var agent = PhantomAgentSchema.AgentDefinitionFromJson(content)
                ?? throw new InvalidOperationException("Failed to deserialize agent definition from JSON.");
            return RehydrateGithubCliBuiltinTools(agent, content);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in {sourceLabel}: {ex.Message}", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid agent definition in {sourceLabel}: {ex.Message}", ex);
        }
    }

    private static AgentDefinition RehydrateGithubCliBuiltinTools(AgentDefinition agent, string content)
    {
        if (agent is not PromptAgent promptAgent || promptAgent.Tools is null)
        {
            return agent;
        }

        using var jsonDocument = JsonDocument.Parse(content);
        if (!jsonDocument.RootElement.TryGetProperty("tools", out var toolsElement)
            || toolsElement.ValueKind != JsonValueKind.Array)
        {
            return agent;
        }

        var rawTools = toolsElement.EnumerateArray().ToArray();
        for (var i = 0; i < rawTools.Length && i < promptAgent.Tools.Count; i++)
        {
            var rawTool = rawTools[i];
            if (rawTool.ValueKind != JsonValueKind.Object
                || !rawTool.TryGetProperty("kind", out var kindElement)
                || kindElement.ValueKind != JsonValueKind.String
                || !string.Equals(kindElement.GetString(), GitHubCliBuiltinToolsTool.KindName, StringComparison.Ordinal))
            {
                continue;
            }

            var existing = promptAgent.Tools[i];
            promptAgent.Tools[i] = new GitHubCliBuiltinToolsTool
            {
                Kind = GitHubCliBuiltinToolsTool.KindName,
                Name = existing.Name,
                Description = existing.Description,
                Bindings = existing.Bindings,
                Connection = (existing as CustomTool)?.Connection!,
                Options = (existing as CustomTool)?.Options!,
                AvailableTools = ReadBuiltinToolSet(rawTool, "available-tools"),
                ExcludedTools = ReadBuiltinToolSet(rawTool, "excluded-tools"),
                ClientMode = ReadClientMode(rawTool),
            };
        }

        return agent;
    }

    private static BuiltinToolSet? ReadBuiltinToolSet(JsonElement toolElement, string propertyName)
    {
        if (!TryGetToolPolicyProperty(toolElement, propertyName, out var selector)
            || selector.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        IReadOnlyList<string>? tools = null;
        if (selector.TryGetProperty("tools", out var toolsElement)
            && toolsElement.ValueKind == JsonValueKind.Array)
        {
            tools = [.. toolsElement.EnumerateArray()
                .Where(static element => element.ValueKind == JsonValueKind.String)
                .Select(static element => element.GetString()!)];
        }

        var isolated = selector.TryGetProperty("isolated", out var isolatedElement)
            && isolatedElement.ValueKind == JsonValueKind.True;

        return new BuiltinToolSet(tools, isolated);
    }

    private static GitHub.Copilot.CopilotClientMode ReadClientMode(JsonElement toolElement)
    {
        if (!TryGetToolPolicyProperty(toolElement, "client-mode", out var modeElement)
            || modeElement.ValueKind != JsonValueKind.String)
        {
            return GitHub.Copilot.CopilotClientMode.CopilotCli;
        }

        return string.Equals(modeElement.GetString(), "empty", StringComparison.OrdinalIgnoreCase)
            ? GitHub.Copilot.CopilotClientMode.Empty
            : GitHub.Copilot.CopilotClientMode.CopilotCli;
    }

    private static bool TryGetToolPolicyProperty(JsonElement toolElement, string propertyName, out JsonElement value)
    {
        if (toolElement.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        if (toolElement.TryGetProperty("options", out var options)
            && options.ValueKind == JsonValueKind.Object
            && options.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static AgentDefinition ParseYamlAgent(string content, string sourceLabel)
    {
        try
        {
            return PhantomAgentSchema.AgentDefinitionFromYaml(content)
                ?? throw new InvalidOperationException("Failed to deserialize agent definition from YAML.");
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException($"Invalid YAML in {sourceLabel}: {ex.Message}", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid agent definition in {sourceLabel}: {ex.Message}", ex);
        }
    }

    private static void ValidateJsonAgainstSchema(string content, string sourceLabel)
    {
        try
        {
            using var jsonDocument = JsonDocument.Parse(content);
            var validationResult = AgentDefinitionJsonSchema.Value.Evaluate(jsonDocument.RootElement);
            if (!validationResult.IsValid)
            {
                throw new InvalidOperationException(
                    $"JSON in {sourceLabel} does not match supported AgentDefinition schema.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in {sourceLabel}: {ex.Message}", ex);
        }
    }
}
