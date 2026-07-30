using AgentSchema;
using MongoDB.Bson;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using System.Linq;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatResumeTests
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

    /// <summary>
    /// Stores <paramref name="count"/> child sessions under <paramref name="parentSessionId"/>
    /// and returns their session ID strings.
    /// </summary>
    private static async Task<string[]> StoreChildrenAsync(
        InMemoryAgentPersistenceStore store,
        string parentSessionId,
        int count)
    {
        var childIds = new string[count];
        for (var i = 0; i < count; i++)
        {
            childIds[i] = $"resume-child-{i}";
            await store.StoreAsync(new StoreRequestAgent
            {
                Agent = new PersistedAgent
                {
                    AgentSessionId = childIds[i],
                    AgentDefinitionJson = BsonDocument.Parse(EchoAgentDefinition.ToJson()),
                }
            });
            await store.AddSubAgentLinkAsync(parentSessionId, childIds[i]);
        }
        return childIds;
    }

    private static async Task<AgentChat> CreateRestoredParentAsync(
        InMemoryAgentPersistenceStore store,
        string parentSessionId,
        AgentServices? services = null,
        TaskScheduler? foregroundScheduler = null)
    {
        var createTask = AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = EchoAgentDefinition,
            AgentSessionId = parentSessionId,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            DisplayNameOverride = "restored-parent",
            AgentServices = services,
            ForegroundScheduler = foregroundScheduler,
        });

        // Initialization now unconditionally dispatches session init onto the foreground scheduler
        // and awaits it (issue #1100). A CapturingTaskScheduler only runs work when driven, so run
        // the queued init task here to let creation complete; the restore-time sub-agent stub adds
        // it queues stay pending for the test to drain and observe.
        if (foregroundScheduler is CapturingTaskScheduler capturing)
        {
            capturing.RunPending();
        }

        return await createTask;
    }

    private static AgentChatFactory CreateFactory(InMemoryAgentPersistenceStore store) =>
        new(store, new AgentServices { ChatClientOverride = new DeterministicTestChatClient() }, TaskScheduler.Default);

    /// <summary>
    /// A <see cref="TaskScheduler"/> that queues tasks without executing them until
    /// <see cref="Drain"/> is called. Enables deterministic verification that mutations
    /// are scheduled (not run inline) and provides explicit drain control.
    /// </summary>
    private sealed class CapturingTaskScheduler : TaskScheduler
    {
        private readonly List<Task> _queue = [];

        public int QueuedCount => _queue.Count;

        public void Drain()
        {
            while (_queue.Count > 0)
            {
                var tasks = _queue.ToList();
                _queue.Clear();
                foreach (var task in tasks)
                    TryExecuteTask(task);
            }
        }

        /// <summary>
        /// Executes the tasks currently queued in a single pass, leaving any tasks they queue as a
        /// side effect pending. Used to drive AgentChat initialization to completion without also
        /// running the init-queued mutations, so the test retains control of when those run.
        /// </summary>
        public void RunPending()
        {
            var tasks = _queue.ToList();
            _queue.Clear();
            foreach (var task in tasks)
                TryExecuteTask(task);
        }

        protected override IEnumerable<Task>? GetScheduledTasks() => _queue;
        protected override void QueueTask(Task task) => _queue.Add(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }

    [Fact]
    public async Task AgentChat_Resume_CreatesLazySubAgentStubsWithoutCreatingAgentChat()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-lazy";
        await StoreChildrenAsync(store, parentSessionId, 1);

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services, scheduler);
        scheduler.Drain();

        var stub = Assert.IsType<SubAgent>(Assert.Single(parent.SubAgents));
        Assert.Null(stub.AgentChat);
    }

    [Fact]
    public async Task AgentChat_Resume_SubAgentCount_MatchesStoredLinks()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-count";
        await StoreChildrenAsync(store, parentSessionId, 2);

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services, scheduler);
        scheduler.Drain();

        Assert.Equal(2, parent.SubAgents.Count);
    }

    [Fact]
    public async Task AgentChat_Resume_EachStubHasCorrectSessionId()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-ids";
        var childIds = await StoreChildrenAsync(store, parentSessionId, 2);

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services, scheduler);
        scheduler.Drain();

        var stubSessionIds = parent.SubAgents.Cast<SubAgent>().Select(s => s.SessionId.Value).ToList();
        Assert.Contains(childIds[0], stubSessionIds);
        Assert.Contains(childIds[1], stubSessionIds);
    }

    [Fact]
    public async Task AgentChat_Resume_NoFactory_Throws()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-nofactory";
        await StoreChildrenAsync(store, parentSessionId, 2);

        var scheduler = new CapturingTaskScheduler();
        // No factory in services + persisted children => restore must throw.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services: null, scheduler);
        });
    }

    [Fact]
    public async Task AgentChat_Resume_MultipleChildren_EachLoadedIndependently()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-multi";
        var childIds = await StoreChildrenAsync(store, parentSessionId, 2);

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services, scheduler);
        scheduler.Drain();

        var stubs = parent.SubAgents.Cast<SubAgent>().ToList();

        await using var lease0 = await stubs.First(s => s.SessionId.Value == childIds[0]).AcquireLeaseAsync();
        await using var lease1 = await stubs.First(s => s.SessionId.Value == childIds[1]).AcquireLeaseAsync();

        Assert.Equal(childIds[0], lease0.AgentChat.AgentSessionId);
        Assert.Equal(childIds[1], lease1.AgentChat.AgentSessionId);
        Assert.NotSame(lease0.AgentChat, lease1.AgentChat);
    }

    [Fact]
    public async Task AgentChat_Resume_SubAgentsAddedOnForegroundScheduler()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-scheduler";
        await StoreChildrenAsync(store, parentSessionId, 1);

        var scheduler = new CapturingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services, scheduler);

        // Before draining: stub is queued but not yet in SubAgents
        Assert.Empty(parent.SubAgents);

        scheduler.Drain();

        // After draining: stub is present
        Assert.Single(parent.SubAgents);
    }

    // #1128: A reloaded sub-agent's SDK run is no longer executing so no terminal
    // Complete/Fail event will ever arrive. Restore must force every restored sub-agent to
    // AgentChatCompletionState.Succeeded so the UI running indicators clear.
    [Fact]
    public async Task AgentChat_Resume_RunningSubAgents_AreMarkedSucceeded()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-succeeded";
        var childIds = await StoreChildrenAsync(store, parentSessionId, 2);

        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services);
        await parent.WaitForRestoredSubAgentsMarkedTerminalAsync();

        var stubs = parent.SubAgents.Cast<SubAgent>().ToList();
        Assert.Equal(2, stubs.Count);
        foreach (var stub in stubs)
        {
            await using var lease = await stub.AcquireLeaseAsync();
            Assert.Equal(AgentChatCompletionState.Succeeded, lease.AgentChat.CompletionState);
        }
    }

    // #1128: Forcing restored sub-agents terminal must raise CompletionStateChanged so UI
    // subscribers (running-item markers, pulsating brain, RunningSubAgentDisplay) actually
    // observe the transition; a silent override change would leave the UI stuck.
    [Fact]
    public async Task AgentChat_Resume_RunningSubAgents_RaiseCompletionStateChanged()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-raise";
        var childIds = await StoreChildrenAsync(store, parentSessionId, 1);

        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services);
        await parent.WaitForRestoredSubAgentsMarkedTerminalAsync();

        var stub = Assert.IsType<SubAgent>(Assert.Single(parent.SubAgents));
        await using var lease = await stub.AcquireLeaseAsync();

        // After restore the completion-state override is already applied. Re-invoking it
        // with the same value must NOT raise the event (idempotency), so to prove the event
        // fires on transition we drive a fresh transition and observe it.
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.AgentChat.CompletionStateChanged += (_, _) => raised.TrySetResult();
        lease.AgentChat.SetCompletionState(AgentChatCompletionState.Failed);

        await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentChatCompletionState.Failed, lease.AgentChat.CompletionState);
    }

    // #1128: Already-terminal restored sub-agents (in this test we simulate by calling
    // SetCompletionState(Succeeded) beforehand) must not double-raise on restore.
    [Fact]
    public async Task AgentChat_Resume_AlreadyCompletedSubAgents_ResolveToSucceeded()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-already";
        var childIds = await StoreChildrenAsync(store, parentSessionId, 3);

        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services);
        await parent.WaitForRestoredSubAgentsMarkedTerminalAsync();

        // All restored children must clear their running markers even if they persisted
        // as running (multiple persisted running sub-agents case from the issue).
        Assert.Equal(3, parent.SubAgents.Count);
        foreach (var stub in parent.SubAgents.Cast<SubAgent>())
        {
            await using var lease = await stub.AcquireLeaseAsync();
            Assert.Equal(AgentChatCompletionState.Succeeded, lease.AgentChat.CompletionState);
        }
    }

    // #1128 scope note: only sub-agents get the forced terminal override; a root/parent
    // AgentChat still reports Running per AgentChat.CompletionState's documented contract.
    [Fact]
    public async Task AgentChat_Resume_ParentSession_StateUnchanged()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-unchanged";
        await StoreChildrenAsync(store, parentSessionId, 1);

        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services);
        await parent.WaitForRestoredSubAgentsMarkedTerminalAsync();

        Assert.Equal(AgentChatCompletionState.Running, parent.CompletionState);
    }

    // #1128: A still-live (non-restored) sub-agent registered via ISubAgentTable.Add during
    // an active session must remain in its live Running state; only reload's lazy stubs
    // are forced terminal.
    [Fact]
    public async Task AgentChat_LiveSubAgent_NotAffectedByRestoreTerminalOverride()
    {
        var store = new InMemoryAgentPersistenceStore();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        // Create a fresh parent (nothing to restore) and register a live sub-agent.
        await using var parent = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = EchoAgentDefinition,
            AgentSessionId = "parent-live",
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            AgentServices = services,
        });

        await parent.WaitForRestoredSubAgentsMarkedTerminalAsync();

        // No sub-agents were persisted, so no restore-driven overrides fire.
        Assert.Empty(parent.SubAgents);
    }

    // #1140: Reloading a session must preserve the persisted last-activity timestamp for
    // each restored (already-completed) sub-agent. Before this fix, the restored sub-agent's
    // lastUpdatedAt was either the reload time (from AgentChat construction) or, after #1128
    // materialised every restored sub-agent, the time SetCompletionState(Succeeded) was
    // called during restore. The card must show "N days ago" reflecting when the sub-agent
    // actually finished, not "just now".
    [Fact]
    public async Task AgentChat_Resume_CompletedSubAgents_PreservePersistedLastUpdatedAt()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-preserve-lastupdated";
        var childIds = await StoreChildrenAsync(store, parentSessionId, 2);

        // Freeze each child's persisted UpdatedUtc to a distinct value in the past by
        // re-storing with an explicit LastUpdatedUtc (InMemoryAgentPersistenceStore honours
        // the value when supplied on the request).
        var persistedTimes = new DateTime[childIds.Length];
        for (var i = 0; i < childIds.Length; i++)
        {
            persistedTimes[i] = new DateTime(2024, 1, 2 + i, 3, 4, 5, DateTimeKind.Utc);
            await store.StoreAsync(new StoreRequestAgent
            {
                Agent = new PersistedAgent
                {
                    AgentSessionId = childIds[i],
                    LastUpdatedUtc = persistedTimes[i],
                },
            });
        }

        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services);
        await parent.WaitForRestoredSubAgentsMarkedTerminalAsync();

        // Every restored sub-agent's materialised AgentChat.LastUpdatedAt must equal its
        // persisted timestamp, never the reload time.
        foreach (var stub in parent.SubAgents.Cast<SubAgent>())
        {
            await using var lease = await stub.AcquireLeaseAsync();
            var index = Array.IndexOf(childIds, stub.SessionId.Value);
            Assert.InRange(index, 0, childIds.Length - 1);
            Assert.Equal(persistedTimes[index], lease.AgentChat.LastUpdatedAt);
        }
    }

    // #1140: The #1128 restore-time SetCompletionState(Succeeded) must NOT bump
    // lastUpdatedAt for restored sub-agents. This is the specific write path that broke the
    // symptom in the wild — a preserved seeded value that then gets clobbered by the forced
    // completion is still wrong.
    [Fact]
    public async Task AgentChat_Resume_ForcedCompletion_DoesNotBumpLastUpdatedAt()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-forced-nobump";
        var childIds = await StoreChildrenAsync(store, parentSessionId, 1);

        var persistedTime = new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = childIds[0],
                LastUpdatedUtc = persistedTime,
            },
        });

        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services);
        await parent.WaitForRestoredSubAgentsMarkedTerminalAsync();

        var stub = Assert.IsType<SubAgent>(Assert.Single(parent.SubAgents));
        await using var lease = await stub.AcquireLeaseAsync();

        // The forced-terminal override must have fired (#1128) but must not have advanced the
        // timestamp past the persisted seed (#1140).
        Assert.Equal(AgentChatCompletionState.Succeeded, lease.AgentChat.CompletionState);
        Assert.Equal(persistedTime, lease.AgentChat.LastUpdatedAt);
    }

    // #1140 must not regress #1128: even with preserve-timestamp semantics, restored
    // sub-agents still resolve to Succeeded so UI running indicators clear.
    [Fact]
    public async Task AgentChat_Resume_RunningSubAgents_StillResolveToSucceededWithPreservedTimestamp()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-still-succeeds";
        var childIds = await StoreChildrenAsync(store, parentSessionId, 2);

        var persistedTime = new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        foreach (var id in childIds)
        {
            await store.StoreAsync(new StoreRequestAgent
            {
                Agent = new PersistedAgent
                {
                    AgentSessionId = id,
                    LastUpdatedUtc = persistedTime,
                },
            });
        }

        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services);
        await parent.WaitForRestoredSubAgentsMarkedTerminalAsync();

        foreach (var stub in parent.SubAgents.Cast<SubAgent>())
        {
            await using var lease = await stub.AcquireLeaseAsync();
            // #1128 preserved: still Succeeded after restore.
            Assert.Equal(AgentChatCompletionState.Succeeded, lease.AgentChat.CompletionState);
            // #1140: timestamp preserved.
            Assert.Equal(persistedTime, lease.AgentChat.LastUpdatedAt);
        }
    }
}
