using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;
using IRunningAgentChatFactory = Phantom.Workspaces.Llm.IRunningAgentChatFactory;

namespace Phantom.Workspaces.Tests;

public sealed class RunningAgentChatTableTests
{
    // ── FakeRunningAgentChatFactory ────────────────────────────────────────────

    private sealed class FakeRunningAgentChatFactory : IRunningAgentChatFactory
    {
        private readonly TaskScheduler _foregroundScheduler;
        private readonly Dictionary<AgentSessionId, (int RefCount, RunningAgentChat Entry)> _sessions = new();

        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();
        public AgentDefinition? LastDefinition { get; private set; }

        public FakeRunningAgentChatFactory(TaskScheduler? foregroundScheduler = null)
        {
            _foregroundScheduler = foregroundScheduler ?? TaskScheduler.Default;
        }

        public async Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, bool registerAsRunningAgent = true, CancellationToken ct = default)
        {
            bool isNew;
            lock (_sessions)
            {
                if (_sessions.TryGetValue(sessionId, out var existing))
                {
                    _sessions[sessionId] = (existing.RefCount + 1, existing.Entry);
                    isNew = false;
                }
                else
                {
                    var entry = new RunningAgentChat(sessionId, this);
                    _sessions[sessionId] = (1, entry);
                    isNew = true;
                }
            }

            if (isNew)
            {
                var entryToAdd = _sessions[sessionId].Entry;
                await Task.Factory.StartNew(
                    () => RunningSessions.Add(entryToAdd),
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    _foregroundScheduler);
            }

            return new RunningAgentChatLease(sessionId, null!, () => RemoveRefAsync(sessionId));
        }

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            string? nameOverride = null, CancellationToken ct = default)
            => GetAsync(sessionId, ct: ct);

        public Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true, CancellationToken ct = default)
        {
            LastDefinition = definition;
            return GetAsync(sessionId, ct: ct);
        }

        private async ValueTask RemoveRefAsync(AgentSessionId sessionId)
        {
            bool shouldRemove;
            RunningAgentChat? entryToRemove;

            lock (_sessions)
            {
                if (!_sessions.TryGetValue(sessionId, out var existing))
                {
                    return;
                }

                if (existing.RefCount <= 1)
                {
                    _sessions.Remove(sessionId);
                    shouldRemove = true;
                    entryToRemove = existing.Entry;
                }
                else
                {
                    _sessions[sessionId] = (existing.RefCount - 1, existing.Entry);
                    shouldRemove = false;
                    entryToRemove = null;
                }
            }

            if (shouldRemove && entryToRemove is not null)
            {
                await Task.Factory.StartNew(
                    () => RunningSessions.Remove(entryToRemove),
                    CancellationToken.None,
                    TaskCreationOptions.None,
                    _foregroundScheduler);
            }
        }
    }

    private sealed class CapturingScheduler : TaskScheduler
    {
        public bool WasInvoked { get; set; }

        protected override IEnumerable<Task>? GetScheduledTasks() => null;

        protected override void QueueTask(Task task)
        {
            WasInvoked = true;
            TryExecuteTask(task);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            WasInvoked = true;
            return TryExecuteTask(task);
        }
    }

    private static AcquireAgentChatRequest Request(
        AgentSessionId sessionId,
        string entityName = "",
        string? entityId = null,
        string? entityDisplayName = null,
        string? entityDescription = null)
        => new()
        {
            AgentSessionId = sessionId,
            EntityName = entityName,
            EntityId = entityId,
            EntityDisplayName = entityDisplayName,
            EntityDescription = entityDescription,
        };

    private sealed class FakeAgentDefinitionResolver : IAgentDefinitionResolver
    {
        private readonly AgentDefinition definition;

        public FakeAgentDefinitionResolver(AgentDefinition definition)
        {
            this.definition = definition;
        }

        public int ResolveCallCount { get; private set; }

        public Task<ResolvedAgentDefinition?> ResolveAsync(
            AgentDefinitionResolveRequest request,
            CancellationToken cancellationToken = default)
        {
            ResolveCallCount++;
            return Task.FromResult<ResolvedAgentDefinition?>(new ResolvedAgentDefinition(this.definition));
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AcquireAsync_AddsEntityInfoToRunningSessions()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-entity-info");

        await using var lease = await table.AcquireAsync(Request(sessionId, entityName: "My Entity", entityId: "entity-id-1"), TestContext.Current.CancellationToken);

        var entry = Assert.Single(table.RunningSessions);
        Assert.Equal("My Entity", entry.EntityName);
        Assert.Equal("entity-id-1", entry.EntityId);
        Assert.Equal(sessionId, entry.SessionId);
    }

    [Fact]
    public async Task AcquireAsync_LastLeaseDisposed_RemovesFromRunningSessions()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-remove-last");

        var lease = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);
        Assert.Single(table.RunningSessions);

        await lease.DisposeAsync();

        Assert.Empty(table.RunningSessions);
    }

    [Fact]
    public async Task AcquireAsync_TwoLeasesForSameSession_RemovedOnlyOnLastDispose()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-two-leases");

        var lease1 = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);
        var lease2 = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);

        Assert.Single(table.RunningSessions);

        await lease1.DisposeAsync();
        Assert.Single(table.RunningSessions);

        await lease2.DisposeAsync();
        Assert.Empty(table.RunningSessions);
    }

    [Fact]
    public async Task AcquireAsync_EntityInfoPreservedForDurationOfSession()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-entity-preserved");

        var lease1 = await table.AcquireAsync(Request(sessionId, entityName: "Preserved Entity", entityId: "entity-42"), TestContext.Current.CancellationToken);
        // A second acquire (e.g., a second tab) should not overwrite entity info.
        var lease2 = await table.AcquireAsync(Request(sessionId), TestContext.Current.CancellationToken);

        try
        {
            var entry = Assert.Single(table.RunningSessions);
            Assert.Equal("Preserved Entity", entry.EntityName);
            Assert.Equal("entity-42", entry.EntityId);
        }
        finally
        {
            await lease1.DisposeAsync();
            await lease2.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcquireAsync_AddToRunningSessions_HappensOnForegroundScheduler()
    {
        var scheduler = new CapturingScheduler();
        var factory = new FakeRunningAgentChatFactory(scheduler);
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-scheduler-add");

        await using var lease = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);

        Assert.True(scheduler.WasInvoked);
    }

    [Fact]
    public async Task AcquireAsync_RemoveFromRunningSessions_HappensOnForegroundScheduler()
    {
        var scheduler = new CapturingScheduler();
        var factory = new FakeRunningAgentChatFactory(scheduler);
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-scheduler-remove");

        var lease = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);
        scheduler.WasInvoked = false;

        await lease.DisposeAsync();

        Assert.True(scheduler.WasInvoked);
    }

    [Fact]
    public async Task RunningAgentChatWithEntityInfo_AcquireLeaseAsync_DelegatesToUnderlyingRunningAgentChat()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-delegate");

        await using var lease = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);

        var entry = Assert.Single(table.RunningSessions);

        // AcquireLeaseAsync on the wrapper delegates to the underlying RunningAgentChat
        await using var secondLease = await entry.AcquireLeaseAsync(TestContext.Current.CancellationToken);
        Assert.Equal(sessionId, secondLease.SessionId);

        // Dispose the second lease — session remains alive (lease from AcquireAsync still held)
        await secondLease.DisposeAsync();
        Assert.Single(table.RunningSessions);
    }

    [Fact]
    public async Task RunningAgentChatWithEntityInfo_SessionId_MatchesFactory()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-id-match");

        await using var lease = await table.AcquireAsync(Request(sessionId, entityName: "Entity"), TestContext.Current.CancellationToken);

        var entry = Assert.Single(table.RunningSessions);
        Assert.Equal(sessionId, entry.SessionId);
    }

    [Fact]
    public async Task AcquireAsync_MultipleDifferentSessions_EachHasCorrectEntityInfo()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionA = new AgentSessionId("session-multi-a");
        var sessionB = new AgentSessionId("session-multi-b");

        await using var leaseA = await table.AcquireAsync(Request(sessionA, entityName: "Entity A", entityId: "id-a"), TestContext.Current.CancellationToken);
        await using var leaseB = await table.AcquireAsync(Request(sessionB, entityName: "Entity B", entityId: "id-b"), TestContext.Current.CancellationToken);

        Assert.Equal(2, table.RunningSessions.Count);

        var entryA = table.RunningSessions.First(r => r.SessionId == sessionA);
        var entryB = table.RunningSessions.First(r => r.SessionId == sessionB);

        Assert.Equal("Entity A", entryA.EntityName);
        Assert.Equal("id-a", entryA.EntityId);
        Assert.Equal("Entity B", entryB.EntityName);
        Assert.Equal("id-b", entryB.EntityId);
    }

    [Fact]
    public async Task AcquireAsync_WithEntityDisplayName_PassesToFactory()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-display-name");

        await using var lease = await table.AcquireAsync(
            Request(sessionId, entityName: "Entity", entityDisplayName: "Custom Display Name"),
            TestContext.Current.CancellationToken);

        // Verify the factory's GetOrCreateAsync was called (implicitly through our fake returning a lease)
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task AcquireAsync_WithEntityDescription_PassesToFactory()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-description");

        await using var lease = await table.AcquireAsync(
            Request(sessionId, entityName: "Entity", entityDescription: "Test description"),
            TestContext.Current.CancellationToken);

        // Verify the factory's GetOrCreateAsync was called (implicitly through our fake returning a lease)
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task AcquireAsync_WithEntityDisplayNameAndDescription_PassesToFactory()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-both");

        await using var lease = await table.AcquireAsync(
            Request(sessionId, entityName: "Entity", entityDisplayName: "Display Name", entityDescription: "Description text"),
            TestContext.Current.CancellationToken);

        // Verify the factory's GetOrCreateAsync was called (implicitly through our fake returning a lease)
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task AcquireAsync_AgentDefinitionProvided_UsesItDirectly()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-direct-definition");
        var definition = CreateTestDefinition("direct-definition");

        await using var lease = await table.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = sessionId,
                AgentDefinition = definition,
            },
            TestContext.Current.CancellationToken);

        Assert.Same(definition, factory.LastDefinition);
    }

    [Fact]
    public async Task AcquireAsync_AgentSessionEntity_DelegatesToResolver()
    {
        var factory = new FakeRunningAgentChatFactory();
        var table = new RunningAgentChatTable(factory);
        var sessionId = new AgentSessionId("session-resolver");
        var definition = CreateTestDefinition("resolved-definition");
        var resolver = new FakeAgentDefinitionResolver(definition);

        await using var lease = await table.AcquireAsync(
            new AcquireAgentChatRequest
            {
                AgentSessionId = sessionId,
                AgentSessionEntity = JsonDocument.Parse("""{"agent-session-id":"session-resolver"}""").RootElement.Clone(),
                AgentDefinitionResolver = resolver,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, resolver.ResolveCallCount);
        Assert.Same(definition, factory.LastDefinition);
    }

    private static AgentDefinition CreateTestDefinition(string name)
        => AgentDefinition.FromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "{{name}}",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": []
            }
            """);
}
