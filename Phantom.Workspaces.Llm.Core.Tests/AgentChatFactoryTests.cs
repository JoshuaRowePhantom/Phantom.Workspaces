using AgentSchema;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatFactoryTests
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

    private static async Task<InMemoryAgentPersistenceStore> CreatePopulatedStoreAsync(
        AgentSessionId sessionId,
        AgentDefinition? definition = null)
    {
        var store = new InMemoryAgentPersistenceStore();
        var def = definition ?? EchoAgentDefinition;
        var definitionJson = BsonDocument.Parse(def.ToJson());
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

    private static AgentChatFactory CreateFactory(
        IAgentPersistenceStore? store = null,
        DeterministicTestChatClient? client = null,
        TaskScheduler? foregroundScheduler = null)
    {
        store ??= new InMemoryAgentPersistenceStore();
        client ??= new DeterministicTestChatClient();
        var services = new AgentServices { ChatClientOverride = client };
        return new AgentChatFactory(store, services, foregroundScheduler ?? TaskScheduler.Default);
    }

    [Fact]
    public async Task GetAsync_NewSession_LoadsFromPersistenceAndReturnsLease()
    {
        var sessionId = new AgentSessionId("session-1");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetAsync(sessionId);

        Assert.Equal(sessionId, lease.SessionId);
        Assert.NotNull(lease.AgentChat);
    }

    [Fact]
    public async Task GetAsync_NewSession_AppearsInRunningSessions()
    {
        var sessionId = new AgentSessionId("session-2");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetAsync(sessionId);

        Assert.Single(factory.RunningSessions);
        Assert.Equal(sessionId, factory.RunningSessions[0].SessionId);
    }

    [Fact]
    public async Task GetAsync_SessionAlreadyRunning_ReturnsSameAgentChatNewLease()
    {
        var sessionId = new AgentSessionId("session-3");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease1 = await factory.GetAsync(sessionId);
        await using var lease2 = await factory.GetAsync(sessionId);

        Assert.NotSame(lease1, lease2);
        Assert.Same(lease1.AgentChat, lease2.AgentChat);
        Assert.Single(factory.RunningSessions);
    }

    [Fact]
    public async Task GetAsync_AfterLastLeaseDisposed_RemovesFromRunningSessions()
    {
        var sessionId = new AgentSessionId("session-4");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        var lease = await factory.GetAsync(sessionId);
        Assert.Single(factory.RunningSessions);

        await lease.DisposeAsync();

        Assert.Empty(factory.RunningSessions);
    }

    [Fact]
    public async Task GetAsync_AfterLastLeaseDisposed_DisposesAgentChat()
    {
        var sessionId = new AgentSessionId("session-5");
        var store = await CreatePopulatedStoreAsync(sessionId);
        var trackingClient = new DisposalTrackingChatClient();
        var services = new AgentServices { ChatClientOverride = trackingClient };
        await using var factory = new AgentChatFactory(store, services, TaskScheduler.Default);

        var lease = await factory.GetAsync(sessionId);
        Assert.False(trackingClient.Disposed);

        await lease.DisposeAsync();

        Assert.True(trackingClient.Disposed);
    }

    [Fact]
    public async Task GetAsync_MultipleLeases_RemovedOnlyAfterAllDisposed()
    {
        var sessionId = new AgentSessionId("session-6");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        var lease1 = await factory.GetAsync(sessionId);
        var lease2 = await factory.GetAsync(sessionId);

        await lease1.DisposeAsync();
        Assert.Single(factory.RunningSessions);

        await lease2.DisposeAsync();
        Assert.Empty(factory.RunningSessions);
    }

    [Fact]
    public async Task GetAsync_ConcurrentFirstAcquire_OnlyCreatesOneAgentChat()
    {
        var sessionId = new AgentSessionId("session-7");
        var countingStore = new CountingPersistenceStore(await CreatePopulatedStoreAsync(sessionId));
        await using var factory = CreateFactory(store: countingStore);

        var task1 = factory.GetAsync(sessionId);
        var task2 = factory.GetAsync(sessionId);
        var leases = await Task.WhenAll(task1, task2);

        Assert.Equal(1, countingStore.RestoreAsyncCallCount);
        Assert.Same(leases[0].AgentChat, leases[1].AgentChat);

        await leases[0].DisposeAsync();
        await leases[1].DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_PersistsSessionBeforeReturning()
    {
        var sessionId = new AgentSessionId("session-8");
        var store = new InMemoryAgentPersistenceStore();
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.CreateAsync(EchoAgentDefinition, sessionId);

        var persisted = await store.RestoreAsync(
            new RestoreRequest { AgentSessionId = sessionId.Value! });
        Assert.NotNull(persisted);
        Assert.NotNull(persisted.Value.AgentDefinitionJson);
    }

    [Fact]
    public async Task CreateAsync_NewSession_AppearsInRunningSessions()
    {
        var sessionId = new AgentSessionId("session-9");
        await using var factory = CreateFactory();

        await using var lease = await factory.CreateAsync(EchoAgentDefinition, sessionId);

        Assert.Single(factory.RunningSessions);
        Assert.Equal(sessionId, factory.RunningSessions[0].SessionId);
    }

    [Fact]
    public async Task CreateAsync_DuplicateSessionId_ThrowsInvalidOperationException()
    {
        var sessionId = new AgentSessionId("session-10");
        await using var factory = CreateFactory();

        await using var lease = await factory.CreateAsync(EchoAgentDefinition, sessionId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateAsync(EchoAgentDefinition, sessionId));
    }

    [Fact]
    public async Task RunningSessions_Add_HappensOnForegroundScheduler()
    {
        var sessionId = new AgentSessionId("session-11");
        var store = await CreatePopulatedStoreAsync(sessionId);
        var scheduler = new VerifyingScheduler();
        await using var factory = CreateFactory(store: store, foregroundScheduler: scheduler);

        await using var lease = await factory.GetAsync(sessionId);

        Assert.True(scheduler.WasInvoked);
    }

    [Fact]
    public async Task RunningSessions_Remove_HappensOnForegroundScheduler()
    {
        var sessionId = new AgentSessionId("session-12");
        var store = await CreatePopulatedStoreAsync(sessionId);
        var scheduler = new VerifyingScheduler();
        await using var factory = CreateFactory(store: store, foregroundScheduler: scheduler);

        var lease = await factory.GetAsync(sessionId);
        scheduler.WasInvoked = false;

        await lease.DisposeAsync();

        Assert.True(scheduler.WasInvoked);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesAllSessions()
    {
        var id1 = new AgentSessionId("session-13a");
        var id2 = new AgentSessionId("session-13b");
        var trackingClient1 = new DisposalTrackingChatClient();
        var trackingClient2 = new DisposalTrackingChatClient();

        var store = new InMemoryAgentPersistenceStore();
        var def1 = BsonDocument.Parse(EchoAgentDefinition.ToJson());
        var def2 = BsonDocument.Parse(EchoAgentDefinition.ToJson());
        await store.StoreAsync(new StoreRequestAgent { Agent = new PersistedAgent { AgentSessionId = id1.Value!, AgentDefinitionJson = def1 } });
        await store.StoreAsync(new StoreRequestAgent { Agent = new PersistedAgent { AgentSessionId = id2.Value!, AgentDefinitionJson = def2 } });

        var factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = trackingClient1 },
            TaskScheduler.Default);

        var lease1 = await factory.GetAsync(id1);
        factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = trackingClient2 },
            TaskScheduler.Default);
        var lease2 = await factory.GetAsync(id2);

        // Dispose second factory (which owns lease2's session)
        await factory.DisposeAsync();

        Assert.True(trackingClient2.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_AllOutstandingChats_Disposed()
    {
        var id1 = new AgentSessionId("session-14a");
        var id2 = new AgentSessionId("session-14b");
        var store = new InMemoryAgentPersistenceStore();
        var trackingClient = new DisposalTrackingMultipleClient();

        var def = BsonDocument.Parse(EchoAgentDefinition.ToJson());
        await store.StoreAsync(new StoreRequestAgent { Agent = new PersistedAgent { AgentSessionId = id1.Value!, AgentDefinitionJson = def } });
        await store.StoreAsync(new StoreRequestAgent { Agent = new PersistedAgent { AgentSessionId = id2.Value!, AgentDefinitionJson = def } });

        var factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = trackingClient },
            TaskScheduler.Default);

        var lease1 = await factory.GetAsync(id1);
        var lease2 = await factory.GetAsync(id2);

        await factory.DisposeAsync();

        Assert.Equal(2, trackingClient.DisposeCount);
    }

    // ── Foreground-context affinity (issue #909) ─────────────────────────────

    [Fact]
    public async Task GetOrCreateAsync_CalledOffForegroundContext_CreatesChatOnForegroundContext()
    {
        var sessionId = new AgentSessionId("session-fg-1");
        var store = await CreatePopulatedStoreAsync(sessionId);
        using var pump = new AgentChatForegroundContextTests.SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);
        await using var factory = CreateFactory(store: store, foregroundScheduler: scheduler);

        // Callers such as agent tools invoke the factory from thread-pool threads; the factory
        // must schedule chat creation onto its foreground scheduler so the AgentChat
        // constructor's affinity verification is satisfied.
        await using var lease = await Task.Run(() => factory.GetOrCreateAsync(sessionId));

        Assert.NotNull(lease.AgentChat);
        Assert.Same(scheduler, GetForegroundScheduler(lease.AgentChat));
    }

    [Fact]
    public async Task GetAsync_CalledOffForegroundContext_CreatesChatOnForegroundContext()
    {
        var sessionId = new AgentSessionId("session-fg-2");
        var store = await CreatePopulatedStoreAsync(sessionId);
        using var pump = new AgentChatForegroundContextTests.SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);
        await using var factory = CreateFactory(store: store, foregroundScheduler: scheduler);

        await using var lease = await Task.Run(() => factory.GetAsync(sessionId));

        Assert.NotNull(lease.AgentChat);
        Assert.Same(scheduler, GetForegroundScheduler(lease.AgentChat));
    }

    [Fact]
    public async Task CreateAsync_CalledOffForegroundContext_CreatesChatOnForegroundContext()
    {
        var sessionId = new AgentSessionId("session-fg-3");
        using var pump = new AgentChatForegroundContextTests.SingleThreadPump(installSynchronizationContext: true);
        var scheduler = new SynchronizationContextTaskScheduler(pump.Context);
        await using var factory = CreateFactory(foregroundScheduler: scheduler);

        await using var lease = await Task.Run(() => factory.CreateAsync(EchoAgentDefinition, sessionId));

        Assert.NotNull(lease.AgentChat);
        Assert.Same(scheduler, GetForegroundScheduler(lease.AgentChat));
    }

    private static TaskScheduler? GetForegroundScheduler(AgentChat chat)
    {
        var field = typeof(AgentChat).GetField(
            "foregroundScheduler",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        return (TaskScheduler?)field!.GetValue(chat);
    }

    // ── Factory self-injection (issue #1036) ─────────────────────────────────
    //
    // When the factory hands its services to the chats it creates, it must inject
    // itself as the RunningAgentChatFactory. Otherwise a reopened session cannot
    // materialise its persisted sub-agents (RestoreSubAgentsAsync bails out when the
    // factory is null), so the sub-agents tree comes back empty.

    private static AgentServices GetRequestServices(AgentChat chat)
    {
        var requestField = typeof(AgentChat).GetField(
            "request",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(requestField);
        var request = requestField!.GetValue(chat);
        Assert.NotNull(request);

        var servicesProperty = request!.GetType().GetProperty("AgentServices");
        Assert.NotNull(servicesProperty);
        var services = (AgentServices?)servicesProperty!.GetValue(request);
        Assert.NotNull(services);
        return services!;
    }

    private static async Task WaitForSubAgentCountAsync(AgentChat chat, int expected)
    {
        if (chat.SubAgents.Count >= expected)
        {
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (chat.SubAgents.Count >= expected)
            {
                tcs.TrySetResult();
            }
        }

        var incc = (INotifyCollectionChanged)chat.SubAgents;
        incc.CollectionChanged += Handler;
        try
        {
            if (chat.SubAgents.Count >= expected)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await tcs.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            incc.CollectionChanged -= Handler;
        }
    }

    private static async Task StoreChildAsync(
        InMemoryAgentPersistenceStore store,
        string parentSessionId,
        string childSessionId)
    {
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = childSessionId,
                AgentDefinitionJson = BsonDocument.Parse(EchoAgentDefinition.ToJson()),
            }
        });
        await store.AddSubAgentLinkAsync(parentSessionId, childSessionId);
    }

    [Fact]
    public async Task GetAsync_CreatedChat_ExposesFactoryAsRunningAgentChatFactory()
    {
        var sessionId = new AgentSessionId("session-selffactory-get");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetAsync(sessionId);

        var services = GetRequestServices(lease.AgentChat);
        Assert.Same(factory, services.RunningAgentChatFactory);
    }

    [Fact]
    public async Task GetOrCreateAsync_CreatedChat_ExposesFactoryAsRunningAgentChatFactory()
    {
        var sessionId = new AgentSessionId("session-selffactory-getorcreate");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetOrCreateAsync(sessionId);

        var services = GetRequestServices(lease.AgentChat);
        Assert.Same(factory, services.RunningAgentChatFactory);
    }

    [Fact]
    public async Task CreateAsync_CreatedChat_ExposesFactoryAsRunningAgentChatFactory()
    {
        var sessionId = new AgentSessionId("session-selffactory-create");
        await using var factory = CreateFactory();

        await using var lease = await factory.CreateAsync(EchoAgentDefinition, sessionId);

        var services = GetRequestServices(lease.AgentChat);
        Assert.Same(factory, services.RunningAgentChatFactory);
    }

    [Fact]
    public async Task GetAsync_ExplicitFactoryInServices_IsNotOverwritten()
    {
        var sessionId = new AgentSessionId("session-selffactory-explicit");
        var store = await CreatePopulatedStoreAsync(sessionId);
        var explicitFactory = CreateFactory(store: store);
        await using var _ = explicitFactory;
        var services = new AgentServices
        {
            ChatClientOverride = new DeterministicTestChatClient(),
            RunningAgentChatFactory = explicitFactory,
        };
        await using var factory = new AgentChatFactory(store, services, TaskScheduler.Default);

        await using var lease = await factory.GetAsync(sessionId);

        var chatServices = GetRequestServices(lease.AgentChat);
        Assert.Same(explicitFactory, chatServices.RunningAgentChatFactory);
    }

    [Fact]
    public async Task GetAsync_ResumeSessionWithPersistedSubAgents_RestoresThem()
    {
        var parentSessionId = "session-selffactory-resume";
        var store = await CreatePopulatedStoreAsync(new AgentSessionId(parentSessionId));
        await StoreChildAsync(store, parentSessionId, "resume-child-a");
        await StoreChildAsync(store, parentSessionId, "resume-child-b");

        // Bare services (no RunningAgentChatFactory): the factory must self-inject so
        // the reopened parent can materialise its persisted sub-agents.
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetAsync(new AgentSessionId(parentSessionId));

        await WaitForSubAgentCountAsync(lease.AgentChat, 2);
        Assert.Equal(2, lease.AgentChat.SubAgents.Count);
    }

    // ── Test doubles ──────────────────────────────────────────────────────────

    private sealed class DisposalTrackingChatClient : IChatClient, IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DisposalTrackingMultipleClient : IChatClient, IAsyncDisposable
    {
        private int _disposeCount;
        public int DisposeCount => _disposeCount;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingPersistenceStore : IAgentPersistenceStore
    {
        private readonly IAgentPersistenceStore _inner;
        private int _restoreCount;

        public int RestoreAsyncCallCount => _restoreCount;

        public CountingPersistenceStore(IAgentPersistenceStore inner) => _inner = inner;

        public ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
            => _inner.StoreAsync(request, cancellationToken);

        public ValueTask<PersistedAgent?> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _restoreCount);
            return _inner.RestoreAsync(request, cancellationToken);
        }

        public ValueTask<ChatMessage[]> ReadMessagesAsync(ReadMessagesRequest request, CancellationToken cancellationToken = default)
            => _inner.ReadMessagesAsync(request, cancellationToken);

        public ValueTask AddSubAgentLinkAsync(string parentSessionId, string childSessionId, CancellationToken cancellationToken = default)
            => _inner.AddSubAgentLinkAsync(parentSessionId, childSessionId, cancellationToken);

        public ValueTask<IReadOnlyList<AgentSessionId>> ReadSubAgentChildIdsAsync(string parentSessionId, CancellationToken cancellationToken = default)
            => _inner.ReadSubAgentChildIdsAsync(parentSessionId, cancellationToken);
    }

    private sealed class VerifyingScheduler : TaskScheduler
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
}
