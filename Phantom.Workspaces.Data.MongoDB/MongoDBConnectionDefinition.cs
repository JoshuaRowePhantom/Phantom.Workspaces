using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;

namespace Phantom.Workspaces.Data.MongoDB;

 [JsonPolymorphic(TypeDiscriminatorPropertyName = "provider")]
 [JsonDerivedType(typeof(MongoDBContainerConnectionDefinition), "container")]
 [JsonDerivedType(typeof(MongoDBExternalConnectionDefinition), "external")]
public abstract class MongoDBConnectionDefinition
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    [JsonIgnore]
    public abstract MongoDBConnectionProvider Provider { get; }

    public static MongoDBConnectionDefinition CreateContainer(
        string containerName,
        string dataDirectory)
    {
        return new MongoDBContainerConnectionDefinition
        {
            ContainerName = containerName,
            DataDirectory = dataDirectory,
        };
    }

    public static MongoDBConnectionDefinition CreateExternal(
        string connectionString)
    {
        return new MongoDBExternalConnectionDefinition
        {
            ConnectionString = connectionString,
        };
    }

    public string ToJson()
    {
        var json = JsonSerializer.Serialize(this, typeof(MongoDBConnectionDefinition), SerializerOptions);
        return json;
    }

    public static MongoDBConnectionDefinition FromJson(
        string json)
    {
        return JsonSerializer.Deserialize<MongoDBConnectionDefinition>(json, SerializerOptions)
               ?? throw new JsonException("MongoDB connection definition JSON could not be deserialized.");
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
        var result = MongoDBConnectionDefinitionJsonSchema.Value.Evaluate(
            element,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        if (!result.IsValid)
        {
            throw new JsonException("MongoDB connection definition JSON does not match the mongo-db-connection schema.");
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed class MongoDBContainerConnectionDefinition : MongoDBConnectionDefinition
{
    [JsonIgnore]
    public override MongoDBConnectionProvider Provider => MongoDBConnectionProvider.Container;

    [JsonPropertyName("container-name")]
    public string ContainerName { get; init; } = string.Empty;

    [JsonPropertyName("data-directory")]
    public string DataDirectory { get; init; } = string.Empty;
}

public sealed class MongoDBExternalConnectionDefinition : MongoDBConnectionDefinition
{
    [JsonIgnore]
    public override MongoDBConnectionProvider Provider => MongoDBConnectionProvider.External;

    [JsonPropertyName("connection-string")]
    public string ConnectionString { get; init; } = string.Empty;
}

public enum MongoDBConnectionProvider
{
    Container,
    External,
}
