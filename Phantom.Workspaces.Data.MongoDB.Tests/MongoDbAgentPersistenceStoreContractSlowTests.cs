using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Tests;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

/// <summary>
/// Runs the shared agent persistence store contract (store/restore, message ordering, sub-agent links)
/// against a real Atlas Local MongoDB container. All contract cases run automatically by inheritance
/// from <see cref="AgentPersistenceStoreContractTests"/>.
/// </summary>
[Trait("Category", "SlowDocker")]
[Collection(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class MongoDbAgentPersistenceStoreContractSlowTests : AgentPersistenceStoreContractTests
{
    private readonly MongoDbTestDatabaseFixture _fixture;

    public MongoDbAgentPersistenceStoreContractSlowTests(MongoDbTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    protected override ValueTask<IAgentPersistenceStore> CreateStoreAsync()
    {
        return ValueTask.FromResult<IAgentPersistenceStore>(
            new MongoDbAgentPersistenceStore(_fixture.Database, "contract-test-agent-persistence"));
    }

    protected override async ValueTask ResetStoreAsync()
    {
        await _fixture.Database.DropCollectionAsync("contract-test-agent-persistence-sessions");
        await _fixture.Database.DropCollectionAsync("contract-test-agent-persistence-definitions");
        await _fixture.Database.DropCollectionAsync("contract-test-agent-persistence-messages");
        await _fixture.Database.DropCollectionAsync("contract-test-agent-persistence-sub-agent-manifests");
    }
}
