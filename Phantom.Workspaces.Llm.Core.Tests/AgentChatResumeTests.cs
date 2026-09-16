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
        IAgentPersistenceStore store,
        string parentSessionId,
        AgentServices? services = null,
        TaskScheduler? foregroundScheduler = null,
        Action<AgentChat>? onConstructed = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<IAsyncDisposable>? ownedResources = null)
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
            CancellationToken = cancellationToken,
            OwnedResources = ownedResources,
        }, onConstructed);

        return await createTask;
    }

    private static AgentChatFactory CreateFactory(InMemoryAgentPersistenceStore store) =>
        new(store, new AgentServices { ChatClientOverride = new DeterministicTestChatClient() }, TaskScheduler.Default);

    private sealed class ObservingTaskScheduler : TaskScheduler
    {
        protected override IEnumerable<Task>? GetScheduledTasks() => null;
        protected override void QueueTask(Task task) =>
            ThreadPool.QueueUserWorkItem(_ => TryExecuteTask(task));
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }

    [Fact]
    public async Task AgentChat_Resume_CreatesLazySubAgentStubsWithoutCreatingAgentChat()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-lazy";
        await StoreChildrenAsync(store, parentSessionId, 1);

        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services);

        var stub = Assert.IsType<SubAgent>(Assert.Single(parent.SubAgents));
        Assert.Null(stub.AgentChat);
    }

    [Fact]
    public async Task AgentChat_Resume_SubAgentCount_MatchesStoredLinks()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-count";
        await StoreChildrenAsync(store, parentSessionId, 2);

        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services);

        Assert.Equal(2, parent.SubAgents.Count);
    }

    [Fact]
    public async Task AgentChat_Resume_EachStubHasCorrectSessionId()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-ids";
        var childIds = await StoreChildrenAsync(store, parentSessionId, 2);

        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services);

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

        // No factory in services + persisted children => restore must throw.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services: null);
        });
    }

    [Fact]
    public async Task AgentChat_Resume_MultipleChildren_EachLoadedIndependently()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-multi";
        var childIds = await StoreChildrenAsync(store, parentSessionId, 2);

        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        await using var parent = await CreateRestoredParentAsync(store, parentSessionId, services);

        var stubs = parent.SubAgents.Cast<SubAgent>().ToList();

        await using var lease0 = await stubs.First(s => s.SessionId.Value == childIds[0]).AcquireLeaseAsync();
        await using var lease1 = await stubs.First(s => s.SessionId.Value == childIds[1]).AcquireLeaseAsync();

        Assert.Equal(childIds[0], lease0.AgentChat.Information.AgentSessionId);
        Assert.Equal(childIds[1], lease1.AgentChat.Information.AgentSessionId);
        Assert.NotSame(lease0.AgentChat, lease1.AgentChat);
    }

    [Fact]
    public async Task AgentChat_Resume_SubAgentsAddedOnForegroundScheduler()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-scheduler";
        await StoreChildrenAsync(store, parentSessionId, 1);

        var scheduler = new ObservingTaskScheduler();
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };
        var addedOnForeground = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var parent = await CreateRestoredParentAsync(
            store,
            parentSessionId,
            services,
            scheduler,
            chat => ((System.Collections.Specialized.INotifyCollectionChanged)chat.SubAgents)
                .CollectionChanged += (_, _) =>
                    addedOnForeground.TrySetResult(TaskScheduler.Current == scheduler));

        Assert.True(await addedOnForeground.Task);
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
        await parent.RestoreCompleted;

        var stubs = parent.SubAgents.Cast<SubAgent>().ToList();
        Assert.Equal(2, stubs.Count);
        foreach (var stub in stubs)
        {
            await using var lease = await stub.AcquireLeaseAsync();
            Assert.Equal(AgentChatCompletionState.Succeeded, lease.LocalAgentChat.CompletionState);
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
        await parent.RestoreCompleted;

        var stub = Assert.IsType<SubAgent>(Assert.Single(parent.SubAgents));
        await using var lease = await stub.AcquireLeaseAsync();

        // After restore the completion-state override is already applied. Re-invoking it
        // with the same value must NOT raise the event (idempotency), so to prove the event
        // fires on transition we drive a fresh transition and observe it.
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lease.LocalAgentChat.CompletionStateChanged += (_, _) => raised.TrySetResult();
        lease.LocalAgentChat.SetCompletionState(AgentChatCompletionState.Failed);

        await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AgentChatCompletionState.Failed, lease.LocalAgentChat.CompletionState);
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
        await parent.RestoreCompleted;

        // All restored children must clear their running markers even if they persisted
        // as running (multiple persisted running sub-agents case from the issue).
        Assert.Equal(3, parent.SubAgents.Count);
        foreach (var stub in parent.SubAgents.Cast<SubAgent>())
        {
            await using var lease = await stub.AcquireLeaseAsync();
            Assert.Equal(AgentChatCompletionState.Succeeded, lease.LocalAgentChat.CompletionState);
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
        await parent.RestoreCompleted;

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

        await parent.RestoreCompleted;

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
        await parent.RestoreCompleted;

        // Every restored sub-agent's materialised AgentChat.LastUpdatedAt must equal its
        // persisted timestamp, never the reload time.
        foreach (var stub in parent.SubAgents.Cast<SubAgent>())
        {
            await using var lease = await stub.AcquireLeaseAsync();
            var index = Array.IndexOf(childIds, stub.SessionId.Value);
            Assert.InRange(index, 0, childIds.Length - 1);
            Assert.Equal(persistedTimes[index], lease.LocalAgentChat.LastUpdatedAt);
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
        await parent.RestoreCompleted;

        var stub = Assert.IsType<SubAgent>(Assert.Single(parent.SubAgents));
        await using var lease = await stub.AcquireLeaseAsync();

        // The forced-terminal override must have fired (#1128) but must not have advanced the
        // timestamp past the persisted seed (#1140).
        Assert.Equal(AgentChatCompletionState.Succeeded, lease.LocalAgentChat.CompletionState);
        Assert.Equal(persistedTime, lease.LocalAgentChat.LastUpdatedAt);
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
        await parent.RestoreCompleted;

        foreach (var stub in parent.SubAgents.Cast<SubAgent>())
        {
            var lease = await stub.AcquireLeaseAsync();
            Assert.NotNull(lease);
            var restoredChild = lease.LocalAgentChat;
            Assert.NotNull(restoredChild);
            await using (lease)
            {
                // #1128 preserved: still Succeeded after restore.
                Assert.Equal(AgentChatCompletionState.Succeeded, restoredChild.CompletionState);
                // #1140: timestamp preserved.
                Assert.Equal(persistedTime, restoredChild.LastUpdatedAt);
            }
        }
    }

    [Fact]
    public async Task AgentChat_Resume_CreateCompletionPublishesCompleteSnapshotExactlyOnce()
    {
        var store = new InMemoryAgentPersistenceStore();
        var parentSessionId = "parent-atomic-resume";
        await StoreChildrenAsync(store, parentSessionId, 3);
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };
        AgentChat? observed = null;
        var publications = 0;

        await using var parent = await CreateRestoredParentAsync(
            store,
            parentSessionId,
            services,
            onConstructed: chat =>
            {
                observed = chat;
                ((System.Collections.Specialized.INotifyCollectionChanged)chat.SubAgents)
                    .CollectionChanged += (_, _) => Interlocked.Increment(ref publications);
            });

        var initializingChat = Assert.IsType<AgentChat>(observed);
        Assert.Same(initializingChat.RestoreCompleted, initializingChat.RestoreCompleted);
        await initializingChat.RestoreCompleted;
        Assert.True(initializingChat.RestoreCompleted.IsCompletedSuccessfully);
        Assert.Equal(3, publications);
        Assert.Equal(3, parent.SubAgents.Count);
    }

    [Fact]
    public async Task AgentChat_Resume_FailurePreservesOriginalExceptionInReadinessSignal()
    {
        var expected = new ResumeTestException("child-link read failed");
        var store = new ControlledChildReadStore(new InMemoryAgentPersistenceStore(), failure: expected);
        var resource = new TrackingAsyncDisposable();
        AgentChat? observed = null;

        var creation = CreateRestoredParentAsync(
            store,
            "parent-failed-resume",
            onConstructed: chat => observed = chat,
            ownedResources: [resource]);

        var creationError = await Assert.ThrowsAsync<ResumeTestException>(() => creation);
        var initializingChat = Assert.IsType<AgentChat>(observed);
        var readinessError = await Assert.ThrowsAsync<ResumeTestException>(
            () => initializingChat.RestoreCompleted);

        Assert.Same(expected, creationError);
        Assert.Same(expected, readinessError);
        Assert.Empty(initializingChat.SubAgents);
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public async Task AgentChat_Resume_CancellationPublishesCancellationWithoutPartialState()
    {
        var inner = new InMemoryAgentPersistenceStore();
        await StoreChildrenAsync(inner, "parent-cancelled-resume", 2);
        var store = new ControlledChildReadStore(inner, block: true);
        using var cancellation = new CancellationTokenSource();
        AgentChat? observed = null;

        var creation = CreateRestoredParentAsync(
            store,
            "parent-cancelled-resume",
            onConstructed: chat => observed = chat,
            cancellationToken: cancellation.Token);
        await store.ReadStarted;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation);
        var initializingChat = Assert.IsType<AgentChat>(observed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => initializingChat.RestoreCompleted);
        Assert.Empty(initializingChat.SubAgents);
    }

    [Fact]
    public async Task AgentChat_Resume_DisposalDuringReadCancelsAndDrainsInitialization()
    {
        var inner = new InMemoryAgentPersistenceStore();
        await StoreChildrenAsync(inner, "parent-disposed-resume", 2);
        var store = new ControlledChildReadStore(inner, block: true);
        AgentChat? observed = null;

        var creation = CreateRestoredParentAsync(
            store,
            "parent-disposed-resume",
            onConstructed: chat => observed = chat);
        await store.ReadStarted;
        var initializingChat = Assert.IsType<AgentChat>(observed);

        var disposal = initializingChat.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creation);
        await disposal;
        Assert.True(store.CancellationObserved);
        Assert.True(initializingChat.RestoreCompleted.IsCanceled);
        Assert.Empty(initializingChat.SubAgents);
    }

    [Fact]
    public async Task AgentChat_Resume_RecreatedSessionCannotReceiveStalePublication()
    {
        var store = new InMemoryAgentPersistenceStore();
        const string parentSessionId = "parent-recreated-resume";
        await StoreChildrenAsync(store, parentSessionId, 2);
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        var first = await CreateRestoredParentAsync(store, parentSessionId, services);
        await first.DisposeAsync();
        var firstSnapshot = first.SubAgents.ToArray();

        await using var replacement = await CreateRestoredParentAsync(store, parentSessionId, services);
        await replacement.RestoreCompleted;

        Assert.Equal(2, firstSnapshot.Length);
        Assert.Equal(firstSnapshot, first.SubAgents);
        Assert.Equal(2, replacement.SubAgents.Count);
        Assert.DoesNotContain(replacement.SubAgents, firstSnapshot.Contains);
    }

    [Fact]
    public async Task AgentChat_Resume_ConcurrentSessionsHaveIndependentReadiness()
    {
        var store = new InMemoryAgentPersistenceStore();
        await StoreChildrenAsync(store, "parent-concurrent-a", 1);
        await StoreChildrenAsync(store, "parent-concurrent-b", 2);
        await using var factory = CreateFactory(store);
        var services = new AgentServices { RunningAgentChatFactory = factory };

        var firstCreation = CreateRestoredParentAsync(store, "parent-concurrent-a", services);
        var secondCreation = CreateRestoredParentAsync(store, "parent-concurrent-b", services);
        var parents = await Task.WhenAll(firstCreation, secondCreation);
        await using var first = parents[0];
        await using var second = parents[1];

        await Task.WhenAll(first.RestoreCompleted, second.RestoreCompleted);
        Assert.Single(first.SubAgents);
        Assert.Equal(2, second.SubAgents.Count);
        Assert.All(first.SubAgents, item => Assert.StartsWith("resume-child-", item.AgentId));
        Assert.All(second.SubAgents, item => Assert.StartsWith("resume-child-", item.AgentId));
    }

    private sealed class ResumeTestException(string message) : Exception(message);

    private sealed class TrackingAsyncDisposable : IAsyncDisposable
    {
        internal int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            this.DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ControlledChildReadStore(
        IAgentPersistenceStore inner,
        bool block = false,
        Exception? failure = null) : IAgentPersistenceStore
    {
        private readonly TaskCompletionSource readStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task ReadStarted => this.readStarted.Task;
        internal bool CancellationObserved { get; private set; }

        public ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
            => inner.StoreAsync(request, cancellationToken);

        public ValueTask<PersistedAgent?> RestoreAsync(
            RestoreRequest request,
            CancellationToken cancellationToken = default)
            => inner.RestoreAsync(request, cancellationToken);

        public ValueTask<Microsoft.Extensions.AI.ChatMessage[]> ReadMessagesAsync(
            ReadMessagesRequest request,
            CancellationToken cancellationToken = default)
            => inner.ReadMessagesAsync(request, cancellationToken);

        public ValueTask AddSubAgentLinkAsync(
            string parentSessionId,
            string childSessionId,
            CancellationToken cancellationToken = default)
            => inner.AddSubAgentLinkAsync(parentSessionId, childSessionId, cancellationToken);

        public async ValueTask<IReadOnlyList<AgentSessionId>> ReadSubAgentChildIdsAsync(
            string parentSessionId,
            CancellationToken cancellationToken = default)
        {
            this.readStarted.TrySetResult();
            if (block)
            {
                try
                {
                    await this.releaseRead.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    this.CancellationObserved = true;
                    throw;
                }
            }

            if (failure is not null)
            {
                throw failure;
            }

            return await inner.ReadSubAgentChildIdsAsync(parentSessionId, cancellationToken);
        }
    }
}
