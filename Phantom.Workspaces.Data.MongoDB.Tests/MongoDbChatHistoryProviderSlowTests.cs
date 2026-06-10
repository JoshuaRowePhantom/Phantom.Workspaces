using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Tests;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

[Trait("Category", "SlowDocker")]
[Collection(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class MongoDbAgentPersistenceStoreSlowTests : AgentPersistenceStoreContractTests
{
    private readonly MongoDbTestDatabaseFixture _fixture;

    public MongoDbAgentPersistenceStoreSlowTests(MongoDbTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    protected override ValueTask<IAgentPersistenceStore> CreateStoreAsync()
    {
        return ValueTask.FromResult<IAgentPersistenceStore>(new MongoDbAgentPersistenceStore(
            _fixture.Database,
            MongoDbTestDatabaseFixture.ChatHistoryCollectionName));
    }

    protected override async ValueTask ResetStoreAsync()
    {
        await _fixture.ResetCollectionAsync();
    }
}
