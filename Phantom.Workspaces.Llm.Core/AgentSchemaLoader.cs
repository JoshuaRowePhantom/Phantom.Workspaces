using AgentSchema;
using System.Text.Json;
using YamlDotNet.Core;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Loads and parses AgentSchema definitions from JSON or YAML files or content strings.
/// </summary>
public static class AgentSchemaLoader
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
    /// <exception cref="InvalidOperationException">If parsing fails.</exception>
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

    private static AgentDefinition ParseJsonAgent(string content, string filePath)
    {
        try
        {
            return AgentDefinition.FromJson(content)
                ?? throw new InvalidOperationException("Failed to deserialize agent definition from JSON.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in {filePath}: {ex.Message}", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid agent definition in {filePath}: {ex.Message}", ex);
        }
    }

    private static AgentDefinition ParseYamlAgent(string content, string filePath)
    {
        try
        {
            return AgentDefinition.FromYaml(content)
                ?? throw new InvalidOperationException("Failed to deserialize agent definition from YAML.");
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException($"Invalid YAML in {filePath}: {ex.Message}", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid agent definition in {filePath}: {ex.Message}", ex);
        }
    }
}
