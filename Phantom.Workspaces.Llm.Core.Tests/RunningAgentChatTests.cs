using AgentSchema;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Interfaces;
using System.Collections.ObjectModel;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class RunningAgentChatTests
{
    private const string EchoAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "echo-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": []
        }
        """;

    private static AgentDefinition EchoAgentDefinition =>
        AgentDefinitionLoader.LoadAgentFromJson(EchoAgentDefinitionJson);

    private static async Task<InMemoryAgentPersistenceStore> CreatePopulatedStoreAsync(AgentSessionId sessionId)
    {
        var store = new InMemoryAgentPersistenceStore();
        var definitionJson = BsonDocument.Parse(EchoAgentDefinition.ToJson());
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = sessionId.Value!,
                AgentDefinitionJson = definitionJson,
            }
        });
        return store;
    }

    private static AgentChatFactory CreateRealFactory(IAgentPersistenceStore store)
    {
        var client = new DeterministicTestChatClient();
        var services = new AgentServices { ChatClientOverride = client };
        return new AgentChatFactory(store, services, TaskScheduler.Default);
    }

    [Fact]
    public void RunningAgentChat_SessionId_MatchesConstructorArg()
    {
        var sessionId = new AgentSessionId("test-session");
        var factory = new FakeRunningAgentChatFactory();

        var entry = new RunningAgentChat(sessionId, factory);

        Assert.Equal(sessionId, entry.SessionId);
    }

    [Fact]
    public async Task RunningAgentChat_AcquireLeaseAsync_DelegatesToFactory()
    {
        var sessionId = new AgentSessionId("test-session");
        var factory = new FakeRunningAgentChatFactory();
        var entry = new RunningAgentChat(sessionId, factory);

        var lease = await entry.AcquireLeaseAsync();

        Assert.Equal(1, factory.GetAsyncCallCount);
        Assert.Equal(sessionId, factory.LastRequestedSessionId);
        Assert.NotNull(lease);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task RunningAgentChat_AcquireLeaseAsync_PassesCancellationTokenToFactory()
    {
        var sessionId = new AgentSessionId("test-session");
        var factory = new FakeRunningAgentChatFactory();
        var entry = new RunningAgentChat(sessionId, factory);
        using var cts = new CancellationTokenSource();

        await entry.AcquireLeaseAsync(cts.Token);

        Assert.Equal(cts.Token, factory.LastCancellationToken);
    }

    [Fact]
    public async Task AcquireLeaseAsync_ReturnsLeaseWrappingCorrectAgentChat()
    {
        var sessionId = new AgentSessionId("test-session-agentchat");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var realFactory = CreateRealFactory(store);

        // Prime the factory so the session is running and appears in RunningSessions.
        await using var primeLease = await realFactory.GetAsync(sessionId);
        var entry = realFactory.RunningSessions.Single();

        await using var lease = await entry.AcquireLeaseAsync();

        Assert.Same(primeLease.AgentChat, lease.AgentChat);
    }

    [Fact]
    public async Task AcquireLeaseAsync_CalledMultipleTimes_EachCallReturnsNewLease()
    {
        var sessionId = new AgentSessionId("test-session");
        var factory = new FakeRunningAgentChatFactory();
        var entry = new RunningAgentChat(sessionId, factory);

        var lease1 = await entry.AcquireLeaseAsync();
        var lease2 = await entry.AcquireLeaseAsync();

        Assert.NotSame(lease1, lease2);

        // Disposing one lease must not affect the other.
        await lease1.DisposeAsync();
        Assert.Equal(1, factory.ActiveLeaseCount);

        await lease2.DisposeAsync();
        Assert.Equal(0, factory.ActiveLeaseCount);
    }

    [Fact]
    public async Task AcquireLeaseAsync_AfterEviction_ThrowsOrFaults()
    {
        var sessionId = new AgentSessionId("test-session");
        var factory = new FakeRunningAgentChatFactory();
        var entry = new RunningAgentChat(sessionId, factory);

        // Acquire and immediately release the only lease, which triggers eviction.
        var lease = await entry.AcquireLeaseAsync();
        await lease.DisposeAsync();

        await Assert.ThrowsAnyAsync<Exception>(() => entry.AcquireLeaseAsync());
    }

    private sealed class FakeRunningAgentChatFactory : IRunningAgentChatFactory
    {
        public int GetAsyncCallCount { get; private set; }
        public AgentSessionId LastRequestedSessionId { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }
        public int ActiveLeaseCount => _activeLeaseCount;

        private int _activeLeaseCount;
        private bool _evicted;

        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, CancellationToken ct = default)
        {
            if (_evicted)
                throw new ObjectDisposedException(nameof(FakeRunningAgentChatFactory));

            GetAsyncCallCount++;
            LastRequestedSessionId = sessionId;
            LastCancellationToken = ct;

            Interlocked.Increment(ref _activeLeaseCount);
            var lease = new RunningAgentChatLease(sessionId, null!, () =>
            {
                if (Interlocked.Decrement(ref _activeLeaseCount) == 0)
                    _evicted = true;
                return ValueTask.CompletedTask;
            });
            return Task.FromResult(lease);
        }

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            CancellationToken ct = default)
            => GetAsync(sessionId, ct);
    }
}
