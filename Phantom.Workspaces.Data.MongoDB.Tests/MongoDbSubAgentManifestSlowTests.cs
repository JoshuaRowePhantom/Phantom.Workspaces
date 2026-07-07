using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Data.MongoDB.Tests;

[Trait("Category", "SlowDocker")]
[Collection(MongoDbTestDatabaseCollection.CollectionName)]
public sealed class MongoDbSubAgentManifestSlowTests
{
    private readonly MongoDbTestDatabaseFixture _fixture;

    public MongoDbSubAgentManifestSlowTests(MongoDbTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private MongoDbAgentPersistenceStore CreateStore() =>
        new(_fixture.Database, MongoDbTestDatabaseFixture.ChatHistoryCollectionName);

    [Fact]
    public async Task AddSubAgentLink_TwoDistinctChildren_BothPersisted_NoCrash()
    {
        await _fixture.ResetCollectionAsync();
        var store = CreateStore();
        var parent = "parent-session-1";

        await store.AddSubAgentLinkAsync(parent, "child-a");
        await store.AddSubAgentLinkAsync(parent, "child-b");

        var results = await store.ReadSubAgentChildIdsAsync(parent);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Value == "child-a");
        Assert.Contains(results, r => r.Value == "child-b");
    }

    [Fact]
    public async Task AddSubAgentLink_SameParentChildPair_SecondCall_IsIdempotent()
    {
        await _fixture.ResetCollectionAsync();
        var store = CreateStore();
        var parent = "parent-session-2";

        await store.AddSubAgentLinkAsync(parent, "child-x");
        await store.AddSubAgentLinkAsync(parent, "child-x");

        var results = await store.ReadSubAgentChildIdsAsync(parent);
        Assert.Single(results);
        Assert.Equal("child-x", results[0].Value);
    }

    [Fact]
    public async Task ReadSubAgentChildIds_RoundTrips()
    {
        await _fixture.ResetCollectionAsync();
        var store = CreateStore();
        var parent = "parent-session-3";

        await store.AddSubAgentLinkAsync(parent, "child-roundtrip");

        var results = await store.ReadSubAgentChildIdsAsync(parent);
        var result = Assert.Single(results);
        Assert.Equal("child-roundtrip", result.Value);
    }
}
