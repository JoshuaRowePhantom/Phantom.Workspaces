using MongoDB.Driver;
using Phantom.Workspaces.Data.MongoDB;

namespace Phantom.Workspaces.Llm;

public static class FilesystemEditStoreFactory
{
    public static ValueTask<IFilesystemEditStore> CreateAsync(
        string? connectionJson,
        CancellationToken cancellationToken = default)
        => CreateAsync(connectionJson, null, cancellationToken);

    internal static ValueTask<IFilesystemEditStore> CreateAsync(
        string? connectionJson,
        Func<MongoDbConnectionDefinition, CancellationToken, ValueTask<IMongoClient>>? clientFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionJson))
        {
            return ValueTask.FromResult<IFilesystemEditStore>(new InMemoryFilesystemEditStore());
        }

        var definition = ChatHistoryProviderDefinition.FromJson(connectionJson);
        return definition switch
        {
            MongoDbChatHistoryProviderDefinition mongoDefinition => CreateMongoDbStoreAsync(mongoDefinition, clientFactory, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported filesystem edit store provider: {definition.Provider}"),
        };
    }

    private static async ValueTask<IFilesystemEditStore> CreateMongoDbStoreAsync(
        MongoDbChatHistoryProviderDefinition mongoDefinition,
        Func<MongoDbConnectionDefinition, CancellationToken, ValueTask<IMongoClient>>? clientFactory,
        CancellationToken cancellationToken)
    {
        MongoDbConnectionDefinition mongoConnectionDefinition;

        if (mongoDefinition.MongoProvider.Equals("container", StringComparison.OrdinalIgnoreCase))
        {
            mongoConnectionDefinition = MongoDbConnectionDefinition.CreateContainer(
                mongoDefinition.ContainerName!,
                mongoDefinition.DataDirectory!,
                mongoDefinition.DatabaseName,
                mongoDefinition.CollectionName,
                mongoDefinition.HostPort);
        }
        else if (mongoDefinition.MongoProvider.Equals("external", StringComparison.OrdinalIgnoreCase))
        {
            mongoConnectionDefinition = MongoDbConnectionDefinition.CreateExternal(
                mongoDefinition.ConnectionString!,
                mongoDefinition.DatabaseName,
                mongoDefinition.CollectionName);
        }
        else
        {
            throw new InvalidOperationException($"Unknown MongoDB provider type: {mongoDefinition.MongoProvider}");
        }

        var client = clientFactory is { }
            ? await clientFactory(mongoConnectionDefinition, cancellationToken).ConfigureAwait(false)
            : await new MongoDbConnectionBroker().GetClientAsync(mongoConnectionDefinition, cancellationToken).ConfigureAwait(false);
        if (client is null)
            throw new InvalidOperationException("MongoDB client factory returned no client.");
        var database = client.GetDatabase(mongoDefinition.DatabaseName);
        var collection = database.GetCollection<MongoDbFilesystemEditDocument>(mongoDefinition.CollectionName);
        return new MongoDbFilesystemEditStore(collection);
    }
}
