using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;

namespace Phantom.Workspaces.Containers;

public sealed class ContainerDefinition
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    [JsonPropertyName("container-name")]
    public string ContainerName { get; init; } = string.Empty;

    [JsonPropertyName("image-name")]
    public string ImageName { get; init; } = string.Empty;

    // A stable container hostname. When set, the engine pins the container's hostname via
    // `docker create --hostname`, so identity-sensitive workloads (e.g. the Atlas Local single-node
    // replica set, whose member host is derived from the hostname) stay stable across recreations
    // and image refreshes instead of floating with the ephemeral container id (issue #1415).
    [JsonPropertyName("hostname")]
    public string Hostname { get; init; } = string.Empty;

    [JsonPropertyName("network-type")]
    public ContainerNetworkType NetworkType { get; init; }

    [JsonPropertyName("environment-variables")]
    public Dictionary<string, string> EnvironmentVariables { get; init; } = new();

    [JsonPropertyName("mounts")]
    public List<ContainerMountDefinition> Mounts { get; init; } = new();

    [JsonPropertyName("port-mappings")]
    public List<ContainerPortMappingDefinition> PortMappings { get; init; } = new();

    public string ToJson()
    {
        var json = JsonSerializer.Serialize(this, SerializerOptions);
        ValidateJson(json);
        return json;
    }

    public static ContainerDefinition FromJson(
        string json)
    {
        using var document = JsonDocument.Parse(json);
        ValidateJson(document.RootElement);

        return JsonSerializer.Deserialize<ContainerDefinition>(json, SerializerOptions)
               ?? throw new JsonException("Container definition JSON could not be deserialized.");
    }

    private static void ValidateJson(
        string json)
    {
        using var document = JsonDocument.Parse(json);
        ValidateJson(document.RootElement);
    }

    private static void ValidateJson(
        JsonElement element)
    {
        var result = ContainerDefinitionJsonSchema.Value.Evaluate(
            element,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        if (!result.IsValid)
        {
            throw new JsonException("Container definition JSON does not match the container-definition schema.");
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            DictionaryKeyPolicy = null,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public enum ContainerNetworkType
{
    Bridge,
    Host,
    None,
    Container,
}

public sealed class ContainerMountDefinition
{
    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; init; } = string.Empty;

    [JsonPropertyName("read-only")]
    public bool ReadOnly { get; init; }
}

public sealed class ContainerPortMappingDefinition
{
    [JsonPropertyName("source-port")]
    public int SourcePort { get; init; }

    [JsonPropertyName("target-port")]
    public int TargetPort { get; init; }
}
