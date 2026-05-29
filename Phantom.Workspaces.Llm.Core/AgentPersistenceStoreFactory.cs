using Phantom.Workspaces.Data.MongoDB;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

public static class AgentPersistenceStoreFactory
{
    public static ValueTask<IAgentPersistenceStore> CreateAsync(
        ChatHistoryProviderDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return definition switch
        {
            MongoDbChatHistoryProviderDefinition mongoDefinition => CreateMongoDbStoreAsync(mongoDefinition, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported chat history provider type: {definition.Provider}")
        };
    }

    private static async ValueTask<IAgentPersistenceStore> CreateMongoDbStoreAsync(
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
        return new MongoDbAgentPersistenceStore(database, mongoDefinition.CollectionName);
    }
}
