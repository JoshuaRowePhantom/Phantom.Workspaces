using System.Text.Json;
using AgentSchema;
using Json.Schema;
using YamlDotNet.Core;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Loads and parses supported <see cref="AgentManifest"/> content from JSON or YAML.
///
/// IMPORTANT: Keep <c>JsonSchemas\agent-manifest.json</c> in sync with the entities and
/// fields this loader supports. Any expansion of the supported manifest shape must include
/// corresponding schema updates to preserve validation behavior.
/// </summary>
public static class AgentManifestLoader
{
    /// <summary>
    /// Loads an agent manifest from a JSON or YAML file.
    /// </summary>
    /// <param name="filePath">Path to the manifest file (.json or .yaml/.yml).</param>
    /// <returns>The loaded agent manifest.</returns>
    /// <exception cref="FileNotFoundException">If the file does not exist.</exception>
    /// <exception cref="InvalidOperationException">If the file format is not supported or parsing fails.</exception>
    public static AgentManifest LoadManifest(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Agent manifest file not found: {filePath}");
        }

        var content = File.ReadAllText(filePath);
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        return extension switch
        {
            ".json" => ParseJsonManifest(content, filePath),
            ".yaml" or ".yml" => ParseYamlManifest(content, filePath),
            _ => throw new InvalidOperationException(
                $"Unsupported file format: {extension}. Use .json, .yaml, or .yml files.")
        };
    }

    /// <summary>
    /// Loads an agent manifest from a JSON string.
    /// </summary>
    /// <param name="json">The JSON content.</param>
    /// <returns>The loaded agent manifest.</returns>
    /// <exception cref="InvalidOperationException">If parsing or schema validation fails.</exception>
    public static AgentManifest LoadManifestFromJson(string json)
    {
        return ParseJsonManifest(json, "<json-content>");
    }

    /// <summary>
    /// Loads an agent manifest from a YAML string.
    /// </summary>
    /// <param name="yaml">The YAML content.</param>
    /// <returns>The loaded agent manifest.</returns>
    /// <exception cref="InvalidOperationException">If parsing fails.</exception>
    public static AgentManifest LoadManifestFromYaml(string yaml)
    {
        return ParseYamlManifest(yaml, "<yaml-content>");
    }

    private static AgentManifest ParseJsonManifest(string content, string sourceLabel)
    {
        try
        {
            ValidateJsonAgainstSchema(content, sourceLabel);

            return PhantomAgentSchema.AgentManifestFromJson(content)
                ?? throw new InvalidOperationException("Failed to deserialize agent manifest from JSON.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in {sourceLabel}: {ex.Message}", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid agent manifest in {sourceLabel}: {ex.Message}", ex);
        }
    }

    private static AgentManifest ParseYamlManifest(string content, string sourceLabel)
    {
        try
        {
            return PhantomAgentSchema.AgentManifestFromYaml(content)
                ?? throw new InvalidOperationException("Failed to deserialize agent manifest from YAML.");
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException($"Invalid YAML in {sourceLabel}: {ex.Message}", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid agent manifest in {sourceLabel}: {ex.Message}", ex);
        }
    }

    private static void ValidateJsonAgainstSchema(string content, string sourceLabel)
    {
        try
        {
            using var jsonDocument = JsonDocument.Parse(content);
            var validationResult = AgentManifestJsonSchema.Value.Evaluate(jsonDocument.RootElement);
            if (!validationResult.IsValid)
            {
                throw new InvalidOperationException(
                    $"JSON in {sourceLabel} does not match supported AgentManifest schema.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in {sourceLabel}: {ex.Message}", ex);
        }
    }
}
