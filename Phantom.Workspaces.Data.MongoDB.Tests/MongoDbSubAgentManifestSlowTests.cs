using MongoDB.Bson;
using Phantom.Workspaces.Llm;
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

    private static SubAgentManifestEntry MakeEntry(string sessionId, AgentChatCompletionState state = AgentChatCompletionState.Running) =>
        new()
        {
            SessionId = sessionId,
            AgentDefinitionJson = new BsonDocument { { "key", sessionId } },
            CompletionState = state,
            LastUpdatedAt = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc),
        };

    [Fact]
    public async Task WriteSubAgentManifestEntryAsync_TwoDistinctChildren_BothPersistedNoCrash()
    {
        await _fixture.ResetCollectionAsync();
        var store = CreateStore();
        var parent = "parent-session-1";

        await store.WriteSubAgentManifestEntryAsync(parent, MakeEntry("child-a"));
        await store.WriteSubAgentManifestEntryAsync(parent, MakeEntry("child-b"));

        var results = await store.ReadSubAgentManifestAsync(parent);
        Assert.Equal(2, results.Length);
        Assert.Contains(results, r => r.SessionId == "child-a");
        Assert.Contains(results, r => r.SessionId == "child-b");
    }

    [Fact]
    public async Task WriteSubAgentManifestEntryAsync_SameParentChildPair_SecondCallIsIdempotent()
    {
        await _fixture.ResetCollectionAsync();
        var store = CreateStore();
        var parent = "parent-session-2";
        var entry = MakeEntry("child-x");

        await store.WriteSubAgentManifestEntryAsync(parent, entry);
        await store.WriteSubAgentManifestEntryAsync(parent, entry);

        var results = await store.ReadSubAgentManifestAsync(parent);
        Assert.Single(results);
        Assert.Equal("child-x", results[0].SessionId);
    }

    [Fact]
    public async Task WriteSubAgentManifestEntryAsync_ThenRead_RoundTripsAllFields()
    {
        await _fixture.ResetCollectionAsync();
        var store = CreateStore();
        var parent = "parent-session-3";
        var definition = new BsonDocument { { "type", "test-agent" }, { "version", 42 } };
        var lastUpdated = new DateTime(2024, 7, 15, 9, 30, 0, DateTimeKind.Utc);
        var entry = new SubAgentManifestEntry
        {
            SessionId = "child-roundtrip",
            AgentDefinitionJson = definition,
            CompletionState = AgentChatCompletionState.Succeeded,
            LastUpdatedAt = lastUpdated,
        };

        await store.WriteSubAgentManifestEntryAsync(parent, entry);

        var results = await store.ReadSubAgentManifestAsync(parent);
        Assert.Single(results);
        var result = results[0];
        Assert.Equal("child-roundtrip", result.SessionId);
        Assert.Equal(AgentChatCompletionState.Succeeded, result.CompletionState);
        Assert.Equal(lastUpdated, result.LastUpdatedAt);
        Assert.Equal(definition, result.AgentDefinitionJson);
    }

    [Fact]
    public async Task WriteSubAgentManifestEntryAsync_ThreeChildren_AllReturned()
    {
        await _fixture.ResetCollectionAsync();
        var store = CreateStore();
        var parent = "parent-session-4";

        await store.WriteSubAgentManifestEntryAsync(parent, MakeEntry("child-1"));
        await store.WriteSubAgentManifestEntryAsync(parent, MakeEntry("child-2"));
        await store.WriteSubAgentManifestEntryAsync(parent, MakeEntry("child-3"));

        var results = await store.ReadSubAgentManifestAsync(parent);
        Assert.Equal(3, results.Length);
        Assert.Contains(results, r => r.SessionId == "child-1");
        Assert.Contains(results, r => r.SessionId == "child-2");
        Assert.Contains(results, r => r.SessionId == "child-3");
    }

    [Fact]
    public async Task ReadSubAgentManifestAsync_UnknownParent_ReturnsEmpty()
    {
        await _fixture.ResetCollectionAsync();
        var store = CreateStore();

        var results = await store.ReadSubAgentManifestAsync("unknown-parent-session");

        Assert.Empty(results);
    }

    [Fact]
    public async Task ReadSubAgentManifestAsync_DoesNotReturnEntriesForOtherParents()
    {
        await _fixture.ResetCollectionAsync();
        var store = CreateStore();
        var parentA = "parent-session-a";
        var parentB = "parent-session-b";

        await store.WriteSubAgentManifestEntryAsync(parentA, MakeEntry("child-of-a"));

        var resultsForB = await store.ReadSubAgentManifestAsync(parentB);
        Assert.Empty(resultsForB);

        var resultsForA = await store.ReadSubAgentManifestAsync(parentA);
        Assert.Single(resultsForA);
    }
}
