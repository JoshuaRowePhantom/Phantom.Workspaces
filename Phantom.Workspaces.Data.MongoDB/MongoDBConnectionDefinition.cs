using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;

namespace Phantom.Workspaces.Data.MongoDB;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "provider")]
[JsonDerivedType(typeof(MongoDbContainerConnectionDefinition), "container")]
[JsonDerivedType(typeof(MongoDbExternalConnectionDefinition), "external")]
public abstract class MongoDbConnectionDefinition
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    [JsonIgnore]
    public abstract MongoDbConnectionProvider Provider { get; }

    public static MongoDbConnectionDefinition CreateContainer(
        string containerName,
        string dataDirectory,
        string databaseName,
        string collectionName,
        int? hostPort = null)
    {
        return new MongoDbContainerConnectionDefinition
        {
            ContainerName = containerName,
            DataDirectory = dataDirectory,
            DatabaseName = databaseName,
            CollectionName = collectionName,
            HostPort = hostPort,
        };
    }

    public static MongoDbConnectionDefinition CreateExternal(
        string connectionString,
        string databaseName,
        string collectionName)
    {
        return new MongoDbExternalConnectionDefinition
        {
            ConnectionString = connectionString,
            DatabaseName = databaseName,
            CollectionName = collectionName,
        };
    }

    public string ToJson()
    {
        var json = JsonSerializer.Serialize(this, typeof(MongoDbConnectionDefinition), SerializerOptions);
        ValidateJson(json);
        return json;
    }

    public static MongoDbConnectionDefinition FromJson(
        string json)
    {
        ValidateJson(json);
        return JsonSerializer.Deserialize<MongoDbConnectionDefinition>(json, SerializerOptions)
               ?? throw new JsonException("MongoDb connection definition JSON could not be deserialized.");
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
        var result = MongoDbConnectionDefinitionJsonSchema.Value.Evaluate(
            element,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        if (!result.IsValid)
        {
            throw new JsonException("MongoDb connection definition JSON does not match the mongo-db-connection schema.");
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

public sealed class MongoDbContainerConnectionDefinition : MongoDbConnectionDefinition
{
    [JsonIgnore]
    public override MongoDbConnectionProvider Provider => MongoDbConnectionProvider.Container;

    [JsonPropertyName("container-name")]
    public string ContainerName { get; init; } = string.Empty;

    [JsonPropertyName("data-directory")]
    public string DataDirectory { get; init; } = string.Empty;

    [JsonPropertyName("database-name")]
    public string DatabaseName { get; init; } = string.Empty;

    [JsonPropertyName("collection-name")]
    public string CollectionName { get; init; } = string.Empty;

    [JsonPropertyName("host-port")]
    public int? HostPort { get; init; }

    /// <summary>
    /// Optional container image override. Defaults to
    /// <see cref="MongoDbContainerDefinitionGenerator.DefaultMongoImageName"/> (Atlas Local, which
    /// supports <c>$vectorSearch</c>). Set to a community <c>mongo</c> image to opt out of the
    /// bundled search process.
    /// </summary>
    [JsonPropertyName("image-name")]
    public string? ImageName { get; init; }

    /// <summary>Returns a copy of this definition with the given data directory.</summary>
    public MongoDbContainerConnectionDefinition WithDataDirectory(string dataDirectory)
    {
        return new MongoDbContainerConnectionDefinition
        {
            ContainerName = this.ContainerName,
            DataDirectory = dataDirectory,
            DatabaseName = this.DatabaseName,
            CollectionName = this.CollectionName,
            HostPort = this.HostPort,
            ImageName = this.ImageName,
        };
    }
}

public sealed class MongoDbExternalConnectionDefinition : MongoDbConnectionDefinition
{
    [JsonIgnore]
    public override MongoDbConnectionProvider Provider => MongoDbConnectionProvider.External;

    [JsonPropertyName("connection-string")]
    public string ConnectionString { get; init; } = string.Empty;

    [JsonPropertyName("database-name")]
    public string DatabaseName { get; init; } = string.Empty;

    [JsonPropertyName("collection-name")]
    public string CollectionName { get; init; } = string.Empty;
}

public enum MongoDbConnectionProvider
{
    Container,
    External,
}
