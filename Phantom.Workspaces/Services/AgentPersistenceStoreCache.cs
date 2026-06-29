using System.Runtime.CompilerServices;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Services;

public sealed class AgentPersistenceStoreCache : IAgentPersistenceStoreCache
{
    private const string AgentSessionCollectionSuffix = "-agent-sessions";
    private readonly ConditionalWeakTable<RepositorySource, Task<IAgentPersistenceStore>> cache = new();

    public Task<IAgentPersistenceStore> GetOrCreateAsync(RepositorySource repositorySource)
        => this.cache.GetValue(repositorySource, static src => CreateAgentPersistenceStoreAsync(src));

    private static Task<IAgentPersistenceStore> CreateAgentPersistenceStoreAsync(RepositorySource repositorySource)
    {
        if (repositorySource is not MongoDbRepositorySource mongoSource
            || string.IsNullOrWhiteSpace(mongoSource.ContainerName)
            || string.IsNullOrWhiteSpace(mongoSource.RootCollectionName))
        {
            return Task.FromResult(AgentPersistenceStoreFactory.CreateInMemory());
        }

        var mongoDbDataDirectory = mongoSource.DataDirectory ?? string.Empty;
        var mongoDbDatabaseName = string.IsNullOrWhiteSpace(mongoSource.DatabaseName)
            ? "phantom-workspaces"
            : mongoSource.DatabaseName;
        var agentSessionCollectionName = $"{mongoSource.RootCollectionName}{AgentSessionCollectionSuffix}";
        var chatHistoryProviderDefinition = ChatHistoryProviderDefinition.CreateMongoDb(
            provider: "container",
            databaseName: mongoDbDatabaseName,
            collectionName: agentSessionCollectionName,
            containerName: mongoSource.ContainerName,
            dataDirectory: mongoDbDataDirectory,
            hostPort: mongoSource.HostPort);
        return AgentPersistenceStoreFactory.CreateAsync(chatHistoryProviderDefinition).AsTask();
    }
}
