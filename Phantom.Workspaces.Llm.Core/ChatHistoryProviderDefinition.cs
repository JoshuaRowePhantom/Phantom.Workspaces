using System.Text.Json;
using System.Text.Json.Serialization;
using Json.Schema;

namespace Phantom.Workspaces.Llm;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "provider")]
[JsonDerivedType(typeof(MongoDbChatHistoryProviderDefinition), "mongodb")]
public abstract class ChatHistoryProviderDefinition
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    [JsonIgnore]
    public abstract ChatHistoryProviderType Provider { get; }

    public static ChatHistoryProviderDefinition CreateMongoDb(
        string provider,
        string databaseName,
        string collectionName,
        string? containerName = null,
        string? dataDirectory = null,
        int? hostPort = null,
        string? connectionString = null)
    {
        if (provider.Equals("container", StringComparison.OrdinalIgnoreCase))
        {
            return new MongoDbChatHistoryProviderDefinition
            {
                MongoProvider = "container",
                DatabaseName = databaseName,
                CollectionName = collectionName,
                ContainerName = containerName ?? throw new ArgumentNullException(nameof(containerName)),
                DataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory)),
                HostPort = hostPort,
            };
        }
        else if (provider.Equals("external", StringComparison.OrdinalIgnoreCase))
        {
            return new MongoDbChatHistoryProviderDefinition
            {
                MongoProvider = "external",
                DatabaseName = databaseName,
                CollectionName = collectionName,
                ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString)),
            };
        }
        else
        {
            throw new ArgumentException($"Unknown MongoDB provider type: {provider}", nameof(provider));
        }
    }

    public string ToJson()
    {
        var json = JsonSerializer.Serialize(this, typeof(ChatHistoryProviderDefinition), SerializerOptions);
        ValidateJson(json);
        return json;
    }

    public static ChatHistoryProviderDefinition FromJson(string json)
    {
        ValidateJson(json);
        return JsonSerializer.Deserialize<ChatHistoryProviderDefinition>(json, SerializerOptions)
               ?? throw new JsonException("Chat history provider definition JSON could not be deserialized.");
    }

    public static ChatHistoryProviderDefinition FromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Chat history provider definition file not found: {filePath}");
        }

        var json = File.ReadAllText(filePath);
        return FromJson(json);
    }

    private static void ValidateJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        ValidateJson(document.RootElement);
    }

    private static void ValidateJson(JsonElement element)
    {
        var result = ChatHistoryProviderDefinitionJsonSchema.Value.Evaluate(
            element,
            new EvaluationOptions
            {
                OutputFormat = OutputFormat.Hierarchical,
                RequireFormatValidation = false,
            });

        if (!result.IsValid)
        {
            throw new JsonException("Chat history provider definition JSON does not match the chat-history-provider schema.");
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

public sealed class MongoDbChatHistoryProviderDefinition : ChatHistoryProviderDefinition
{
    [JsonIgnore]
    public override ChatHistoryProviderType Provider => ChatHistoryProviderType.MongoDB;

    [JsonPropertyName("mongoProvider")]
    public string MongoProvider { get; init; } = string.Empty;

    [JsonPropertyName("database-name")]
    public string DatabaseName { get; init; } = string.Empty;

    [JsonPropertyName("collection-name")]
    public string CollectionName { get; init; } = string.Empty;

    // Container provider fields
    [JsonPropertyName("container-name")]
    public string? ContainerName { get; init; }

    [JsonPropertyName("data-directory")]
    public string? DataDirectory { get; init; }

    [JsonPropertyName("host-port")]
    public int? HostPort { get; init; }

    // External provider fields
    [JsonPropertyName("connection-string")]
    public string? ConnectionString { get; init; }
}

public enum ChatHistoryProviderType
{
    MongoDB,
}
