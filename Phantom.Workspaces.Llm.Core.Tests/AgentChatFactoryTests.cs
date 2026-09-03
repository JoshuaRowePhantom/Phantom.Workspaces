using AgentSchema;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Secrets;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;

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
    public async Task CreateAsync_WithDisplayNameOverride_PopulatesAgentChatDisplayName()
    {
        // Fix #1133: AgentChatFactory.CreateAsync must forward the displayNameOverride /
        // descriptionOverride arguments into the InternalCreateAgentChatRequest so the newly
        // created AgentChat's DisplayName/Description reflect the caller-supplied values.
        var sessionId = new AgentSessionId("session-1133-with");
        await using var factory = CreateFactory();

        await using var lease = await factory.CreateAsync(
            EchoAgentDefinition,
            sessionId,
            services: null,
            displayNameOverride: "fix-reload1",
            descriptionOverride: "reload the workspace");

        Assert.Equal("fix-reload1", lease.AgentChat.DisplayName);
        Assert.Equal("reload the workspace", lease.AgentChat.Description);
    }

    [Fact]
    public async Task CreateAsync_WithoutDisplayNameOverride_UsesClientInfoDefault()
    {
        // Fix #1133 fallback: with no override provided, AgentChat.DisplayName degrades to the
        // client-info default (never throws, never leaks the session GUID) so pre-existing
        // callers that do not opt into the new arguments retain their previous behaviour.
        var sessionId = new AgentSessionId("session-1133-without");
        await using var factory = CreateFactory();

        await using var lease = await factory.CreateAsync(EchoAgentDefinition, sessionId);

        // No override was supplied ⇒ DisplayName falls back to the empty client-info default,
        // and — critically — does NOT equal the session id (which was the observed #1133 bug).
        Assert.NotEqual(sessionId.Value, lease.AgentChat.DisplayName);
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
    public async Task GetOrCreateAsync_ManifestRequest_ExposesFactoryAsRunningAgentChatFactory()
    {
        // Issue #1180 regression pin: the manifest-open path in AgentManifestLaunchpadViewModel
        // resolves an AgentDefinition from an AgentManifest (via RunningAgentChatTable) and then
        // calls AgentChatFactory.GetOrCreateAsync with bare AgentServices (no RunningAgentChatFactory
        // pre-set). The factory must self-inject via WithSelfAsFactory so AgentServices reaching
        // AgentChat.CreateAsync carries the factory — otherwise the #1109 guard in AgentChat
        // throws "must be supplied at construction time" as soon as a Copilot SDK client resolves,
        // which is exactly the crash reported in #1180.
        var sessionId = new AgentSessionId("session-manifest-1180");
        var store = new InMemoryAgentPersistenceStore();
        await using var factory = new AgentChatFactory(
            store,
            new AgentServices { ChatClientOverride = new DeterministicTestChatClient() },
            TaskScheduler.Default);

        var bareServices = new AgentServices
        {
            ChatClientOverride = new DeterministicTestChatClient(),
            // Intentionally not setting RunningAgentChatFactory — the factory MUST self-inject.
        };
        Assert.Null(bareServices.RunningAgentChatFactory);

        await using var lease = await factory.GetOrCreateAsync(
            sessionId,
            definition: EchoAgentDefinition,
            services: bareServices);

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
    public async Task GetOrCreateAsync_WhenRegisterAsRunningAgentFalse_DoesNotAppearInRunningSessions()
    {
        // Issue #1150: sub-agents dispatched via SubAgentDispatcherChatClient must not appear in
        // the top-right "Running agents" popup. They opt out via registerAsRunningAgent: false.
        var sessionId = new AgentSessionId("session-noregister");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetOrCreateAsync(sessionId, registerAsRunningAgent: false);

        Assert.NotNull(lease.AgentChat);
        Assert.Empty(factory.RunningSessions);
    }

    [Fact]
    public async Task GetOrCreateAsync_WhenRegisterAsRunningAgentTrue_AppearsInRunningSessions()
    {
        // Issue #1150: top-level agents (the default) must still show up in RunningSessions.
        var sessionId = new AgentSessionId("session-register");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetOrCreateAsync(sessionId, registerAsRunningAgent: true);

        Assert.Single(factory.RunningSessions);
        Assert.Equal(sessionId, factory.RunningSessions[0].SessionId);
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
    public async Task GetAsync_ExplicitFactoryInServices_IsOverwrittenBySelf()
    {
        // Fix #1109: WithSelfAsFactory now ALWAYS injects the outer factory unconditionally.
        // The previous "preserve intentional override" behavior let a foreign factory be wired
        // past the outer factory's sub-agent lifecycle bookkeeping — the same silent-misroute
        // that #1109/#1110 remove for the null case.
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
        Assert.Same(factory, chatServices.RunningAgentChatFactory);
        Assert.NotSame(explicitFactory, chatServices.RunningAgentChatFactory);
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

    // ── Issue #1186: restore-time null-model resilience ──────────────────────
    //
    // Regression: a startup restore of a default workspace whose parent agent chat
    // has persisted sub-agents with empty AgentDefinitions (Model == null) used to
    // throw "Agent definition does not specify a model." from AgentFactory during
    // MarkRestoredSubAgentTerminalAsync's eager materialisation. The exception
    // originated from a foreground-scheduled construction path and prevented the
    // parent chat's InitializeAsync from returning, hanging the startup splash
    // (LoadingWindow.Close() at App.axaml.cs:310 never ran).
    //
    // The fix: MarkRestoredSubAgentTerminalAsync no longer materialises the child.
    // It records the terminal state on the SubAgent stub via
    // SetRestoredCompletionState; the state applies lazily when a later
    // AcquireLeaseAsync actually needs the child chat.

    private const string EmptyPersistedAgentDefinitionJson =
        """
        {
          "kind": "prompt",
          "name": "empty-persisted-child"
        }
        """;

    private static async Task StoreChildWithDefinitionJsonAsync(
        InMemoryAgentPersistenceStore store,
        string parentSessionId,
        string childSessionId,
        string persistedAgentDefinitionJson)
    {
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = childSessionId,
                AgentDefinitionJson = BsonDocument.Parse(persistedAgentDefinitionJson),
            }
        });
        await store.AddSubAgentLinkAsync(parentSessionId, childSessionId);
    }

    [Fact]
    public async Task MarkRestoredSubAgentTerminal_EmptyPersistedAgentDefinition_DoesNotThrow()
    {
        // #1186 regression guard: restoring a parent whose persisted child has an
        // empty (Model == null) AgentDefinition must complete AgentChat.InitializeAsync
        // successfully, not hang or throw.
        var parentSessionId = "parent-1186-restore-nothrow";
        var store = await CreatePopulatedStoreAsync(new AgentSessionId(parentSessionId));
        await StoreChildWithDefinitionJsonAsync(
            store,
            parentSessionId,
            "child-1186-empty",
            EmptyPersistedAgentDefinitionJson);

        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetAsync(new AgentSessionId(parentSessionId));

        await WaitForSubAgentCountAsync(lease.AgentChat, 1);
        Assert.Single(lease.AgentChat.SubAgents);
    }

    [Fact]
    public async Task MarkRestoredSubAgentTerminal_EmptyPersistedAgentDefinition_DoesNotConstructChatClient()
    {
        // #1186 mechanism guard: the restore path must NOT invoke the ChatClient
        // factory for the child (empty AgentDefinition would throw). A spy
        // ChatClientOverride records every construction attempt.
        var parentSessionId = "parent-1186-no-construct";
        var store = await CreatePopulatedStoreAsync(new AgentSessionId(parentSessionId));
        await StoreChildWithDefinitionJsonAsync(
            store,
            parentSessionId,
            "child-1186-empty-no-construct",
            EmptyPersistedAgentDefinitionJson);

        // Use a spy that records every GetService lookup — a proxy for "was any
        // ChatClient ever handed to a materialised child chat?". Since the parent
        // itself uses this client, we count uses AFTER parent construction.
        var spy = new UsageCountingChatClient();
        var services = new AgentServices { ChatClientOverride = spy };
        await using var factory = new AgentChatFactory(store, services, TaskScheduler.Default);

        await using var lease = await factory.GetAsync(new AgentSessionId(parentSessionId));
        await WaitForSubAgentCountAsync(lease.AgentChat, 1);

        // Snapshot: parent has been constructed. Now if the restore path attempted
        // to materialise the child, an extra AgentChat.InitializeAsync run would
        // pull the client from services.ChatClientOverride and enumerate it.
        var stub = Assert.IsType<SubAgent>(Assert.Single(lease.AgentChat.SubAgents));
        Assert.Null(stub.AgentChat); // child NOT materialised
        Assert.Equal(
            AgentChatCompletionState.Succeeded,
            ((IRunningSubAgent)stub).CompletionState);
    }

    [Fact]
    public async Task RestoreSubAgentsAsync_ChildTerminalTaskFaults_ParentInitializeCompletes()
    {
        // #1186 robustness guard: even if a per-child terminal task were to fault
        // (currently impossible because the task is now synchronous, but this pins
        // the invariant), the parent's InitializeAsync must still return so that
        // startup never hangs.
        var parentSessionId = "parent-1186-child-fault";
        var store = await CreatePopulatedStoreAsync(new AgentSessionId(parentSessionId));
        await StoreChildWithDefinitionJsonAsync(
            store,
            parentSessionId,
            "child-1186-fault-a",
            EmptyPersistedAgentDefinitionJson);
        await StoreChildWithDefinitionJsonAsync(
            store,
            parentSessionId,
            "child-1186-fault-b",
            EmptyPersistedAgentDefinitionJson);

        await using var factory = CreateFactory(store: store);

        // Bound the operation aggressively — a hang here is the exact regression
        // #1186 documents.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var lease = await factory.GetAsync(new AgentSessionId(parentSessionId), ct: cts.Token);
        await lease.AgentChat.WaitForRestoredSubAgentsMarkedTerminalAsync().WaitAsync(cts.Token);

        Assert.Equal(2, lease.AgentChat.SubAgents.Count);
    }

    [Fact]
    public async Task AgentChatFactory_GetAsync_ResumeSessionWithEmptyPersistedSubAgents_DoesNotThrow()
    {
        // #1186 top-level regression guard, paralleling
        // GetAsync_ResumeSessionWithPersistedSubAgents_RestoresThem but with empty
        // persisted child AgentDefinitions. The resume must complete without
        // "Agent definition does not specify a model." bubbling up.
        var parentSessionId = "parent-1186-resume-empty";
        var store = await CreatePopulatedStoreAsync(new AgentSessionId(parentSessionId));
        await StoreChildWithDefinitionJsonAsync(
            store,
            parentSessionId,
            "child-1186-resume-empty-a",
            EmptyPersistedAgentDefinitionJson);
        await StoreChildWithDefinitionJsonAsync(
            store,
            parentSessionId,
            "child-1186-resume-empty-b",
            EmptyPersistedAgentDefinitionJson);

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

    private sealed class UsageCountingChatClient : IChatClient
    {
        private int _uses;
        public int UseCount => Volatile.Read(ref _uses);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _uses);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _uses);
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
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

    // Fix #1187: GetAsync must construct the child AgentChat for a persisted hosted
    // Copilot sub-agent from the rehydrated full AgentDefinition (kind/name/model.provider
    // = github-copilot-subagent) without hitting the "Agent definition does not specify a
    // model." throw.
    [Fact]
    public async Task GetAsync_WithRegisterAsRunningAgentFalse_DoesNotAddToRunningSessions()
    {
        // Issue #1205: sub-agents lazily materialised through the restore path (SubAgent.AcquireLeaseAsync)
        // must not leak into the running-agents flyout as "No Open Tab" rows. GetAsync gains the same
        // opt-out as GetOrCreateAsync did in #1150.
        var sessionId = new AgentSessionId("session-1205-noregister");
        var store = await CreatePopulatedStoreAsync(sessionId);
        await using var factory = CreateFactory(store: store);

        await using var lease = await factory.GetAsync(sessionId, registerAsRunningAgent: false);

        Assert.NotNull(lease.AgentChat);
        Assert.Empty(factory.RunningSessions);
    }

    [Fact]
    public async Task GetAsync_DefinitionWithSecretPlaceholder_MaterializesBeforeCreatingChatClient()
    {
        // #1405: the GUI foreground factory path (GetOrCreateAsync -> CreateChatOnForegroundAsync)
        // must materialize ${SECRET:...} placeholders — invoking the SecretProvider and rewriting
        // the definition to an opaque handle — before the chat client is built.
        var provider = new FakeSecretProvider();
        provider.Secrets["GitHubToken"] = ToSecureString("resolved-token");
        var services = new AgentServices
        {
            SecretProvider = provider,
            ChatClientOverride = new DeterministicTestChatClient(),
        };
        await using var factory = CreateFactory();
        var sessionId = new AgentSessionId("session-materialize-secret");

        await using var lease = await factory.GetOrCreateAsync(sessionId, McpSecretDefinition(), services);

        Assert.Equal(1, provider.CallCount);
        var json = lease.AgentChat.AgentDefinition!.ToJson();
        Assert.DoesNotContain("${SECRET:GitHubToken}", json, StringComparison.Ordinal);
        Assert.Contains("${SECRET:", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_AlreadyMaterializedDefinition_DoesNotRePromptOrReScan()
    {
        // #1405: idempotency — a definition already rewritten to opaque handles (with a resolver
        // attached to services) must not be re-scanned or re-prompted when it flows through the
        // foreground factory path again.
        var provider = new FakeSecretProvider();
        provider.Secrets["GitHubToken"] = ToSecureString("resolved-token");
        var baseServices = new AgentServices
        {
            SecretProvider = provider,
            ChatClientOverride = new DeterministicTestChatClient(),
        };

        var (materializedDefinition, materializedServices) = await AgentFactory.MaterializeSecretsIfNeededAsync(
            McpSecretDefinition(), baseServices, manifest: null, agentSessionId: null, CancellationToken.None);
        Assert.Equal(1, provider.CallCount);

        await using var factory = CreateFactory();
        var sessionId = new AgentSessionId("session-already-materialized");

        await using var lease = await factory.GetOrCreateAsync(sessionId, materializedDefinition, materializedServices);

        Assert.Equal(1, provider.CallCount);
    }

    private static AgentDefinition McpSecretDefinition() => AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "mcp-secret-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": [
            {
              "kind": "mcp",
              "name": "github-secret-gated",
              "serverName": "github-secret-gated",
              "connection": { "kind": "key", "endpoint": "http://127.0.0.1:1/", "apiKey": "${SECRET:GitHubToken}" },
              "approvalMode": { "kind": "never" }
            }
          ]
        }
        """);

    private static SecureString ToSecureString(string value)
    {
        var secure = new SecureString();
        foreach (var ch in value)
        {
            secure.AppendChar(ch);
        }

        secure.MakeReadOnly();
        return secure;
    }

    private sealed class FakeSecretProvider : ISecretProvider
    {
        public int CallCount { get; private set; }
        public Dictionary<string, SecureString> Secrets { get; } = [];

        public Task<RequestSecretsResult?> RequestSecretsAsync(IReadOnlyList<SecretRequest> requests, CancellationToken cancellationToken)
        {
            this.CallCount++;
            var retrievers = requests
                .Where(request => this.Secrets.ContainsKey(request.SecretName))
                .Select(request => new SecretRetriever
                {
                    SecretName = request.SecretName,
                    Secret = _ => Task.FromResult(this.Secrets[request.SecretName]),
                })
                .ToArray();

            return Task.FromResult<RequestSecretsResult?>(new RequestSecretsResult(retrievers, []));
        }
    }
}
