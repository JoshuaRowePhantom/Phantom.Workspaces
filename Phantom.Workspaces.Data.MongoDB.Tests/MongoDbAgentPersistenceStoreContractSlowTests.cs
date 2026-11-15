using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Tests;
using MongoDB.Bson;
using Microsoft.Extensions.AI;

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

    [Fact]
    public async Task AgentChat_RetirementPending_RealMongoRetainsFollowingTasksFinalAndContinuation()
    {
        await this.ResetStoreAsync();
        await Phantom.Workspaces.Llm.Core.Tests.CopilotSdkRetirementPersistenceScenario.RunAsync(
            () => new MongoDbAgentPersistenceStore(this._fixture.Database, "contract-test-agent-persistence"));
    }

    [Fact]
    public async Task StoreAsync_CompletedChildTaskAndRootFinal_FreshReadRetainsBoth()
    {
        await this.ResetStoreAsync();
        var store = await this.CreateStoreAsync();
        var productIdentity = "durable-product-" + Guid.NewGuid().ToString("N");
        var providerIdentity = "newer-provider-" + Guid.NewGuid().ToString("N");
        var agent = new PersistedAgent
        {
            AgentSessionId = productIdentity,
            CopilotSdkSessionId = providerIdentity,
            AgentDefinitionJson = BsonDocument.Parse("""{"kind":"prompt","name":"durable"}"""),
        };
        await store.StoreAsync(new()
        {
            Agent = agent,
            NewMessages = [new(ChatRole.Assistant, "older than current provider journal")],
        });
        await store.StoreAsync(new()
        {
            Agent = agent,
            NewMessages =
            [
                new(ChatRole.Assistant, [new FunctionCallContent("completed-task", "task")]),
                new(ChatRole.Tool, [new FunctionResultContent("completed-task", "completed result")]),
                new(ChatRole.Assistant, "durable final"),
            ],
        });
        // A metadata-only/initialization snapshot is not a transcript replacement.
        await store.StoreAsync(new()
        {
            Agent = agent with { AgentSessionJson = new BsonDocument() },
            NewMessages = [],
        });
        var freshStore = new MongoDbAgentPersistenceStore(this._fixture.Database, "contract-test-agent-persistence");
        var restored = await freshStore.ReadMessagesAsync(new() { AgentSessionId = productIdentity });
        Assert.Equal(4, restored.Length);
        Assert.Equal("older than current provider journal", restored[0].Text);
        Assert.Equal("completed-task", Assert.Single(restored[1].Contents.OfType<FunctionCallContent>()).CallId);
        Assert.Equal("completed-task", Assert.Single(restored[2].Contents.OfType<FunctionResultContent>()).CallId);
        Assert.Equal("durable final", restored[3].Text);
        Assert.Equal(providerIdentity, (await freshStore.RestoreAsync(new() { AgentSessionId = productIdentity }))!.Value.CopilotSdkSessionId);
        Assert.Empty(await freshStore.ReadMessagesAsync(new() { AgentSessionId = providerIdentity }));
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
