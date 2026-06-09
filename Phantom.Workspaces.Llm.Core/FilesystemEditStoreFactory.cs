using MongoDB.Driver;
using Phantom.Workspaces.Data.MongoDB;

namespace Phantom.Workspaces.Llm;

public static class FilesystemEditStoreFactory
{
    public static ValueTask<IFilesystemEditStore> CreateAsync(
        string? connectionJson,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionJson))
        {
            return ValueTask.FromResult<IFilesystemEditStore>(new InMemoryFilesystemEditStore());
        }

        var definition = ChatHistoryProviderDefinition.FromJson(connectionJson);
        return definition switch
        {
            MongoDbChatHistoryProviderDefinition mongoDefinition => CreateMongoDbStoreAsync(mongoDefinition, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported filesystem edit store provider: {definition.Provider}"),
        };
    }

    private static async ValueTask<IFilesystemEditStore> CreateMongoDbStoreAsync(
        MongoDbChatHistoryProviderDefinition mongoDefinition,
        CancellationToken cancellationToken)
    {
        MongoDBConnectionDefinition mongoConnectionDefinition;

        if (mongoDefinition.MongoProvider.Equals("container", StringComparison.OrdinalIgnoreCase))
        {
            mongoConnectionDefinition = MongoDBConnectionDefinition.CreateContainer(
                mongoDefinition.ContainerName!,
                mongoDefinition.DataDirectory!,
                mongoDefinition.DatabaseName,
                mongoDefinition.CollectionName,
                mongoDefinition.HostPort);
        }
        else if (mongoDefinition.MongoProvider.Equals("external", StringComparison.OrdinalIgnoreCase))
        {
            mongoConnectionDefinition = MongoDBConnectionDefinition.CreateExternal(
                mongoDefinition.ConnectionString!,
                mongoDefinition.DatabaseName,
                mongoDefinition.CollectionName);
        }
        else
        {
            throw new InvalidOperationException($"Unknown MongoDB provider type: {mongoDefinition.MongoProvider}");
        }

        var broker = new MongoConnectionBroker();
        var client = await broker.GetClientAsync(mongoConnectionDefinition, cancellationToken).ConfigureAwait(false);
        var database = client.GetDatabase(mongoDefinition.DatabaseName);
        var collection = database.GetCollection<MongoDbFilesystemEditDocument>(mongoDefinition.CollectionName);
        return new MongoDbFilesystemEditStore(collection);
    }
}
