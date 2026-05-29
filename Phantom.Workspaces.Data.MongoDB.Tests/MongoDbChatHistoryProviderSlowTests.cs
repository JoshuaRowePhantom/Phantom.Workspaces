using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Tests;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

[Trait("Category", "SlowDocker")]
[Collection(MongoTestDatabaseCollection.CollectionName)]
public sealed class MongoDbAgentPersistenceStoreSlowTests : AgentPersistenceStoreContractTests
{
    private readonly MongoTestDatabaseFixture _fixture;

    public MongoDbAgentPersistenceStoreSlowTests(MongoTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    protected override ValueTask<IAgentPersistenceStore> CreateStoreAsync()
    {
        return ValueTask.FromResult<IAgentPersistenceStore>(new MongoDbAgentPersistenceStore(
            _fixture.Database,
            MongoTestDatabaseFixture.ChatHistoryCollectionName));
    }

    protected override async ValueTask ResetStoreAsync()
    {
        await _fixture.ResetCollectionAsync();
    }
}
