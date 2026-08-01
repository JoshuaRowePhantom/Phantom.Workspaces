using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Tests;
using MongoDB.Bson;

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

    // Fix #1187: legacy hosted Copilot sub-agent rows have no AgentDefinitionJson (the row
    // was never written, or the two-field synthetic round-tripped as null). RestoreAsync
    // detects "session is a sub-agent" from the sub-agent-manifest and substitutes the
    // canonical full hosted-Copilot sub-agent definition so downstream construction never
    // faults on a null Model (the empty-definition cause behind #1186).
    [Fact]
    public async Task RestoreAsync_HostedSubAgentWithMissingDefinition_ReturnsCanonicalDefaultJson()
    {
        await this.ResetStoreAsync();
        var store = await this.CreateStoreAsync();

        var parentSessionId = "parent-1187-mongo";
        var childSessionId = "child-1187-mongo";

        // Write the child session document but never write its AgentDefinition.
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = childSessionId,
                AgentSessionJson = BsonDocument.Parse(
                    $$"""{"session-id":"{{childSessionId}}"}"""),
                AgentDefinitionJson = null,
            },
        }, CancellationToken.None);

        // Register the parent→child link so the substitution fast-path fires.
        await store.AddSubAgentLinkAsync(parentSessionId, childSessionId, CancellationToken.None);

        var restored = await store.RestoreAsync(
            new RestoreRequest { AgentSessionId = childSessionId },
            CancellationToken.None);

        Assert.NotNull(restored);
        Assert.NotNull(restored!.Value.AgentDefinitionJson);
        var definition = AgentSchema.AgentDefinition.FromJson(
            restored.Value.AgentDefinitionJson!.ToJson());
        var promptAgent = Assert.IsType<AgentSchema.PromptAgent>(definition);
        Assert.Equal(
            CopilotSubAgentDefinitionDefaults.HostedSubAgentProvider,
            promptAgent.Model?.Provider);
    }
}
