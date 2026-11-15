using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Tests;
using MongoDB.Bson;
using Microsoft.Extensions.AI;
using MongoDB.Driver;

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
    public async Task StoreAsync_LegacyMessagesWithoutSequenceCounter_ContinuesAfterExistingMaximum()
    {
        await this.ResetStoreAsync();
        var store = await this.CreateStoreAsync();
        var agent = new PersistedAgent { AgentSessionId = Guid.NewGuid().ToString(), AgentSessionJson = new() };
        await store.StoreAsync(new() { Agent = agent, NewMessages = [new(ChatRole.User, "legacy")] });
        var messages = this._fixture.Database.GetCollection<BsonDocument>("contract-test-agent-persistence-messages");
        var legacy = await messages.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        var sequenceField = legacy.Names.Single(name => name.Equals("Sequence", StringComparison.OrdinalIgnoreCase));
        legacy[sequenceField] = 7L;
        await messages.ReplaceOneAsync(new BsonDocument("_id", legacy["_id"]), legacy);
        var sessions = this._fixture.Database.GetCollection<BsonDocument>("contract-test-agent-persistence-sessions");
        var session = await sessions.Find(FilterDefinition<BsonDocument>.Empty).SingleAsync();
        var counterField = session.Names.Single(name => name.Equals("NextSequence", StringComparison.OrdinalIgnoreCase));
        await sessions.UpdateOneAsync(new BsonDocument("_id", session["_id"]), new BsonDocument("$unset", new BsonDocument(counterField, 1)));
        await store.StoreAsync(new() { Agent = agent, NewMessages = [new(ChatRole.Assistant, "after legacy")] });
        var persisted = await messages.Find(FilterDefinition<BsonDocument>.Empty).Sort(new BsonDocument(sequenceField, 1)).ToListAsync();
        Assert.Equal([7L, 8L], persisted.Select(document => document[sequenceField].ToInt64()).ToArray());
        Assert.Equal(["legacy", "after legacy"], (await store.ReadMessagesAsync(new() { AgentSessionId = agent.AgentSessionId })).Select(message => message.Text).ToArray());
    }

    [Fact]
    public async Task StoreAsync_ConcurrentStoreInstances_AllocateUniqueOrderedSequenceRanges()
    {
        await this.ResetStoreAsync();
        var agent = new PersistedAgent { AgentSessionId = Guid.NewGuid().ToString(), AgentSessionJson = new() };
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = 12;
        var writes = Enumerable.Range(0, pending).Select(async index =>
        {
            if (Interlocked.Decrement(ref pending) == 0) admitted.TrySetResult();
            await released.Task;
            var store = new MongoDbAgentPersistenceStore(this._fixture.Database, "contract-test-agent-persistence");
            await store.StoreAsync(new() { Agent = agent, NewMessages = [new(ChatRole.User, $"request-{index}"), new(ChatRole.Assistant, $"answer-{index}")] });
        }).ToArray();
        await admitted.Task;
        released.TrySetResult();
        await Task.WhenAll(writes);
        await this.AssertUniqueSequencesAsync(agent.AgentSessionId, 24);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AgentChat_AcceptedOutputAdmission_RealMongoPreservesEncounterOrderAndFreshRestore(bool continuationBoundary, bool afterReservation = false)
    {
        await this.ResetStoreAsync();
        var productId = await Phantom.Workspaces.Llm.Core.Tests.CopilotSdkRetirementPersistenceScenario.RunAdmissionAsync(
            () => new MongoDbAgentPersistenceStore(this._fixture.Database, "contract-test-agent-persistence"), continuationBoundary, afterReservation);
        var messages = await (await this.CreateStoreAsync()).ReadMessagesAsync(new() { AgentSessionId = productId });
        await this.AssertUniqueSequencesAsync(productId, messages.Length);
    }

    [Fact]
    public async Task AgentChat_PendingAcceptedShutdown_RealMongoDrainsBothBatches()
    {
        await this.ResetStoreAsync();
        var productId = await Phantom.Workspaces.Llm.Core.Tests.CopilotSdkRetirementPersistenceScenario.RunPendingShutdownAsync(
            () => new MongoDbAgentPersistenceStore(this._fixture.Database, "contract-test-agent-persistence"));
        var messages = await (await this.CreateStoreAsync()).ReadMessagesAsync(new() { AgentSessionId = productId });
        await this.AssertUniqueSequencesAsync(productId, messages.Length);
    }

    [Theory]
    [InlineData("observer")]
    [InlineData("retirement")]
    [InlineData("dispatch")]
    [InlineData("poller")]
    [InlineData("secondary-dispatch")]
    public async Task AgentChat_ActiveRootFailure_RealMongoDrainsBeforeRetirement(string entry)
    {
        await this.ResetStoreAsync();
        var productId = await Phantom.Workspaces.Llm.Core.Tests.CopilotSdkRetirementPersistenceScenario.RunActiveFailureDrainAsync(
            () => new MongoDbAgentPersistenceStore(this._fixture.Database, "contract-test-agent-persistence"), entry);
        var messages = await (await this.CreateStoreAsync()).ReadMessagesAsync(new() { AgentSessionId = productId });
        await this.AssertUniqueSequencesAsync(productId, messages.Length);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task AgentChat_AuthoritativeFinalAfterPromotion_RealMongoPreservesDocumentIdentityAndSequence(
        bool prefixExtension, int steering)
    {
        await this.ResetStoreAsync();
        var collection = this._fixture.Database.GetCollection<BsonDocument>("contract-test-agent-persistence-messages");
        BsonDocument? original = null;
        async Task<BsonDocument> ReadCorrectedDocumentAsync()
        {
            var documents = await collection.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            return Assert.Single(documents, document => document.Elements.Any(element =>
                element.Name.Equals("MessageId", StringComparison.OrdinalIgnoreCase) && element.Value == "corrected"));
        }
        var productId = await Phantom.Workspaces.Llm.Core.Tests.CopilotSdkRetirementPersistenceScenario.RunCorrectionAsync(
            () => new MongoDbAgentPersistenceStore(this._fixture.Database, "contract-test-agent-persistence"),
            prefixExtension, steering, async () => { original = await ReadCorrectedDocumentAsync(); });
        var corrected = await ReadCorrectedDocumentAsync();
        Assert.NotNull(original);
        Assert.Equal(original["_id"], corrected["_id"]);
        var sequenceName = original.Names.Single(name => name.Equals("Sequence", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(original[sequenceName], corrected[sequenceName]);
        var messages = await (await this.CreateStoreAsync()).ReadMessagesAsync(new() { AgentSessionId = productId });
        await this.AssertUniqueSequencesAsync(productId, messages.Length);
    }

    private async Task AssertUniqueSequencesAsync(string productId, int count)
    {
        var documents = await this._fixture.Database.GetCollection<BsonDocument>("contract-test-agent-persistence-messages")
            .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
        var sequences = documents.Select(document => document.Elements.Single(element =>
            element.Name.Equals("Sequence", StringComparison.OrdinalIgnoreCase)).Value.ToInt64()).Order().ToArray();
        Assert.Equal(count, sequences.Length);
        Assert.Equal(Enumerable.Range(0, count).Select(index => (long)index), sequences);
        Assert.Equal(count, (await (await this.CreateStoreAsync()).ReadMessagesAsync(new() { AgentSessionId = productId })).Length);
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
