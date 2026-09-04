using System.Threading.Tasks;
using Phantom.Workspaces.Data.Web.Client;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.Tests;

// Issue #1403: the RepositorySource -> IAgentPersistenceStore switch (Web / DevTunnel / MongoDB /
// in-memory) was extracted out of the GUI AgentSessionShortcutContext into
// AgentPersistenceStoreSourceFactory. These tests pin the moved behavior for the branches that are
// safely exercisable without a live dev tunnel or MongoDB container.
public sealed class AgentPersistenceStoreFactoryTests
{
    [Fact]
    public async Task AgentPersistenceStoreFactory_CreateForRepositorySource_Web()
    {
        var store = await AgentPersistenceStoreSourceFactory.CreateForRepositorySourceAsync(
            new WebRepositorySource("https://web.example/agent/"));

        Assert.IsType<WebClientAgentPersistenceStore>(store);
    }

    [Fact]
    public async Task AgentPersistenceStoreFactory_CreateForRepositorySource_Web_BlankEndpointThrows()
    {
        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => AgentPersistenceStoreSourceFactory.CreateForRepositorySourceAsync(
                new WebRepositorySource(string.Empty)));
    }

    [Fact]
    public async Task AgentPersistenceStoreFactory_CreateForRepositorySource_MongoDbAndDevTunnelAndInMemory()
    {
        // Unknown source -> in-memory store.
        var unknownStore = await AgentPersistenceStoreSourceFactory.CreateForRepositorySourceAsync(
            new UnknownRepositorySource());
        Assert.IsType<InMemoryAgentPersistenceStore>(unknownStore);

        // MongoDB source with missing container/collection falls back to the in-memory store (the
        // same guard the old inline switch applied), so no MongoDB container is required.
        var mongoStore = await AgentPersistenceStoreSourceFactory.CreateForRepositorySourceAsync(
            new MongoDbRepositorySource(ContainerName: string.Empty, RootCollectionName: string.Empty));
        Assert.IsType<InMemoryAgentPersistenceStore>(mongoStore);

        // The DevTunnel branch is routed by the same switch; it establishes a live tunnel connection
        // on StartAsync (network + devtunnel CLI), so it is not exercised here. Its construction and
        // reconnection behavior are covered by ReconnectingWebAgentPersistenceStore's own tests.
    }
}
