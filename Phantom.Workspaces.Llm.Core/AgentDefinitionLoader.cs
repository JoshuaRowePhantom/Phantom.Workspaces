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

            return AgentDefinition.FromJson(content)
                ?? throw new InvalidOperationException("Failed to deserialize agent definition from JSON.");
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

    private static AgentDefinition ParseYamlAgent(string content, string sourceLabel)
    {
        try
        {
            return AgentDefinition.FromYaml(content)
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
