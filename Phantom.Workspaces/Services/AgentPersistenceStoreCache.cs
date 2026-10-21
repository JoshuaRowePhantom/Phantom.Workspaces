using System.Runtime.CompilerServices;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Services;

public sealed class AgentPersistenceStoreCache : IAgentPersistenceStoreCache, IAsyncDisposable
{
    private const string AgentSessionCollectionSuffix = "-agent-sessions";
    private readonly ConditionalWeakTable<RepositorySource, Task<IAgentPersistenceStore>> cache = new();
    private readonly IAccessBroker? accessBroker;
    private readonly Func<AccessPresentationScope>? presentationScope;
    private readonly object gate = new();
    private readonly HashSet<Task<IAgentPersistenceStore>> stores = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool stopping;

    public AgentPersistenceStoreCache(
        IAccessBroker? accessBroker = null,
        Func<AccessPresentationScope>? presentationScope = null)
    {
        this.accessBroker = accessBroker;
        this.presentationScope = presentationScope;
    }

    public Task<IAgentPersistenceStore> GetOrCreateAsync(RepositorySource repositorySource)
    {
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.stopping, this);
            return this.cache.GetValue(repositorySource, src =>
            {
                var task = src is WebRepositorySource or DevTunnelNameRepositorySource
                    ? new AgentPersistenceStoreSourceFactory(src, this.accessBroker,
                        presentationScope: this.presentationScope).CreateAsync(this.lifetime.Token)
                    : CreateAgentPersistenceStoreAsync(src);
                this.stores.Add(task);
                return task;
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task<IAgentPersistenceStore>[] pending;
        bool ownsCleanup;
        lock (this.gate)
        {
            ownsCleanup = !this.stopping;
            pending = ownsCleanup ? this.stores.ToArray() : [];
            if (ownsCleanup)
            {
                this.stopping = true;
                this.stores.Clear();
            }
        }

        if (!ownsCleanup)
        {
            await this.disposal.Task.ConfigureAwait(false);
            return;
        }

        var failures = new List<Exception>();
        try { await this.lifetime.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception) { failures.Add(exception); }
        var disposed = new HashSet<IAgentPersistenceStore>(ReferenceEqualityComparer.Instance);
        foreach (var task in pending)
        {
            IAgentPersistenceStore store;
            try
            {
                store = await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (this.lifetime.IsCancellationRequested) { continue; }
            catch (Exception exception) { failures.Add(exception); continue; }
            if (!disposed.Add(store)) { continue; }
            try
            {
                if (store is IAsyncDisposable asynchronous) { await asynchronous.DisposeAsync().ConfigureAwait(false); }
                else if (store is IDisposable synchronous) { synchronous.Dispose(); }
            }
            catch (Exception exception) { failures.Add(exception); }
        }
        this.lifetime.Dispose();
        if (failures.Count > 0)
        {
            var failure = new AggregateException("Persistence cache shutdown did not complete cleanly.", failures);
            this.disposal.TrySetException(failure);
            throw failure;
        }
        this.disposal.TrySetResult();
    }

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
