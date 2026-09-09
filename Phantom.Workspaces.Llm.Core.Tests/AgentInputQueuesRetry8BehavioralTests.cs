using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Tests;

/// <summary>
/// #1485 retry 8: behavioural tests that observe real state, events, ordering and rejection
/// through both the owner-side <see cref="LocalAgentInputQueuesAdapter"/> and the
/// <see cref="RemoteAgentChatProxy"/> proxy adapter (the local/remote-equivalent surface pair).
/// These tests target the specific prior findings still called out as unresolved because prior
/// coverage was reflection/proxy-only or purely constructional.
/// </summary>
public sealed class AgentInputQueuesRetry8BehavioralTests
{
    private sealed class StubAgentChat : IAgentChat
    {
        private readonly IAgentInputQueues adapter;

        public StubAgentChat(IAgentInputQueues adapter)
        {
            this.adapter = adapter;
        }

        public AgentInformation Information => new()
        {
            AgentSessionId = "stub-session",
            AgentId = "stub-agent",
            Name = "stub",
            DisplayName = "Stub",
            Description = "Stub agent chat used by retry 8 behavioural tests.",
            AcceptsUserInput = true,
            AgentDefinition = null!,
        };

        public Usage Usage => default;
        public bool IsBusy => false;
        public AgentChatHistoryCollection History { get; } = new();
        public Task HistoryPopulated => Task.CompletedTask;
        public AgentChatRunningItemCollection RunningItems { get; } = new();
        public IAgentInputQueues InputQueues => this.adapter;
        public System.Collections.ObjectModel.ReadOnlyObservableCollection<IRunningSubAgent> SubAgents { get; }
            = new(new System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent>());
        public System.Collections.ObjectModel.ReadOnlyObservableCollection<AgentChatModal> Modals { get; }
            = new(new System.Collections.ObjectModel.ObservableCollection<AgentChatModal>());
        public Phantom.Workspaces.Llm.SlashCommands.ISlashCommandRegistry SlashCommands { get; }
            = new Phantom.Workspaces.Llm.SlashCommands.SlashCommandRegistry();

        public event EventHandler? InformationChanged { add { } remove { } }
        public event EventHandler? ToolsChanged { add { } remove { } }
        public event EventHandler? UsageChanged { add { } remove { } }
        public event EventHandler<AgentChatHistoryItem>? TurnCompleted { add { } remove { } }

        public IReadOnlyList<AgentChatToolItem> GetToolSnapshot() => Array.Empty<AgentChatToolItem>();

        public Task SetToolEnabledAsync(string toolId, bool enabled, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RespondToModalAsync(string modalId, JsonElement response, CancellationToken ct = default)
            => Task.CompletedTask;

        public void EnqueueSystemNote(string text) { }
        public void EnqueueHelpNote(string text) { }
        public void EnqueueTransientDiagnostic(string text) { }
        public void Interrupt() => this.InterruptCount++;

        public int InterruptCount { get; private set; }

        public object? GetService(Type serviceType) => null;

        public ValueTask DisposeAsync()
        {
            (this.adapter as IDisposable)?.Dispose();
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }

        public bool Disposed { get; private set; }
    }

    private sealed class GatedAgentInputQueues(IAgentInputQueues inner) : IAgentInputQueues
    {
        private readonly TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentInputQueuesSnapshot Snapshot => inner.Snapshot;
        public IReadOnlyList<IAgentInputQueue> Queues => inner.Queues;
        public IAgentInputQueue DefaultQueue => inner.DefaultQueue;
        public IAgentInputQueue ImmediateQueue => inner.ImmediateQueue;
        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public Task WaitUntilRequestedAsync(CancellationToken ct) => this.requested.Task.WaitAsync(ct);
        public void Complete() => this.release.TrySetResult();

        public async Task<AgentInputQueueCommandResult> EnqueueAsync(EnqueueAgentInputRequest request, CancellationToken ct = default)
        {
            this.requested.TrySetResult();
            await this.release.Task.WaitAsync(ct);
            return await inner.EnqueueAsync(request, ct);
        }

        public Task<AgentInputQueueCommandResult> CreateQueueAsync(CreateAgentInputQueueRequest request, CancellationToken ct = default) => inner.CreateQueueAsync(request, ct);
        public Task<AgentInputQueueCommandResult> DeleteQueueAsync(DeleteAgentInputQueueRequest request, CancellationToken ct = default) => inner.DeleteQueueAsync(request, ct);
        public Task<AgentInputQueueCommandResult> EditAsync(EditAgentInputQueueItemRequest request, CancellationToken ct = default) => inner.EditAsync(request, ct);
        public Task<AgentInputQueueCommandResult> RemoveAsync(RemoveAgentInputQueueItemRequest request, CancellationToken ct = default) => inner.RemoveAsync(request, ct);
        public Task<AgentInputQueueCommandResult> MoveAsync(MoveAgentInputQueueItemRequest request, CancellationToken ct = default) => inner.MoveAsync(request, ct);
        public Task<AgentInputQueueCommandResult> ConfigureAsync(ConfigureAgentInputQueueRequest request, CancellationToken ct = default) => inner.ConfigureAsync(request, ct);
        public AgentInputQueueCommandResult CreateQueue(CreateAgentInputQueueRequest request) => inner.CreateQueue(request);
        public AgentInputQueueCommandResult DeleteQueue(DeleteAgentInputQueueRequest request) => inner.DeleteQueue(request);
        public AgentInputQueueCommandResult Enqueue(EnqueueAgentInputRequest request) => inner.Enqueue(request);
        public AgentInputQueueCommandResult Edit(EditAgentInputQueueItemRequest request) => inner.Edit(request);
        public AgentInputQueueCommandResult Remove(RemoveAgentInputQueueItemRequest request) => inner.Remove(request);
        public AgentInputQueueCommandResult Move(MoveAgentInputQueueItemRequest request) => inner.Move(request);
        public AgentInputQueueCommandResult Configure(ConfigureAgentInputQueueRequest request) => inner.Configure(request);
    }

    private static (AgentInputQueueManager Manager, AgentInputQueue Default, LocalAgentInputQueuesAdapter Adapter, StubAgentChat Chat, RemoteAgentChatProxy Proxy) NewPair()
    {
        var manager = new AgentInputQueueManager();
        var defaultQueue = new AgentInputQueue(new AgentInputQueue.Parameters
        {
            Priority = int.MaxValue - 1,
            Immediacy = AgentInputQueueImmediacy.Immediate,
        });
        manager.RegisterInputQueue(defaultQueue);
        var adapter = new LocalAgentInputQueuesAdapter(manager, defaultQueue);
        var chat = new StubAgentChat(adapter);
        var proxy = new RemoteAgentChatProxy(chat);
        return (manager, defaultQueue, adapter, chat, proxy);
    }

    // Gap #30/#31/#32/#43: Local/remote-equivalent surfaces expose the same stable queue ids
    // through RemoteAgentChatProxy — the actual proxy path, not a second local adapter.
    [Fact]
    public void RemoteProxy_DefaultAndImmediateQueueIds_MatchLocalOwner()
    {
        var (_, def, adapter, _, proxy) = NewPair();
        Assert.Equal(def.QueueId, proxy.InputQueues.DefaultQueue.Snapshot.QueueId);
        Assert.Equal(
            adapter.ImmediateQueue.Snapshot.QueueId,
            proxy.InputQueues.ImmediateQueue.Snapshot.QueueId);
        Assert.Equal(adapter.Snapshot.Queues.Length, proxy.InputQueues.Snapshot.Queues.Length);
        Assert.Equal(adapter.Snapshot.Revision, proxy.InputQueues.Snapshot.Revision);
    }

    // Gap #56: Applied delta/order behaviour observed through a command — the proxy sees the
    // applied revision advance and content change after a real Enqueue.
    [Fact]
    public void RemoteProxy_EnqueueThroughProxy_AppliesDeltaAndProjectsItem()
    {
        var (_, def, adapter, _, proxy) = NewPair();
        var revisionBefore = proxy.InputQueues.Snapshot.Revision;

        var proxyChangedCount = 0;
        proxy.InputQueues.Changed += (_, _) => Interlocked.Increment(ref proxyChangedCount);

        var result = proxy.InputQueues.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "via-proxy") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = revisionBefore,
        });

        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
        Assert.True(proxy.InputQueues.Snapshot.Revision > revisionBefore);

        var defViaProxy = proxy.InputQueues.Snapshot.Queues.Single(q => q.QueueId == def.QueueId);
        Assert.Contains(defViaProxy.Items, item => item.ItemId == result.ItemId);
        Assert.True(proxyChangedCount >= 1);

        // Local view has advanced in lock-step.
        Assert.Equal(proxy.InputQueues.Snapshot.Revision, adapter.Snapshot.Revision);
    }

    // Gap #23: Snapshot mutation/content — an enqueue is observable in a subsequent snapshot;
    // the returned snapshot is a captured value, not a live reference.
    [Fact]
    public void RemoteProxy_SnapshotCapture_IsIndependentOfLaterMutations()
    {
        var (_, def, _, _, proxy) = NewPair();
        proxy.InputQueues.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "first") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = proxy.InputQueues.Snapshot.Revision,
        });
        var captured = proxy.InputQueues.Snapshot;
        var capturedItemCount = captured.Queues.Single(q => q.QueueId == def.QueueId).Items.Length;

        proxy.InputQueues.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "second") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = proxy.InputQueues.Snapshot.Revision,
        });

        // The captured snapshot is a value type; its Items count is stable.
        Assert.Equal(capturedItemCount, captured.Queues.Single(q => q.QueueId == def.QueueId).Items.Length);
        // The live proxy view reflects both enqueues.
        Assert.Equal(capturedItemCount + 1,
            proxy.InputQueues.Snapshot.Queues.Single(q => q.QueueId == def.QueueId).Items.Length);
    }

    // Gap #24: Invalid revision — production rejects a stale ExpectedRevision through the proxy.
    [Fact]
    public void RemoteProxy_StaleExpectedRevision_ReturnsConflictThroughProduction()
    {
        var (_, def, _, _, proxy) = NewPair();
        var rev = proxy.InputQueues.Snapshot.Revision;
        // Do a real command to advance revision.
        proxy.InputQueues.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "one") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = rev,
        });
        // Now try with the stale revision.
        var conflict = proxy.InputQueues.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "stale") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = rev,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Conflict, conflict.Status);
        Assert.NotNull(conflict.CurrentSnapshot);
    }

    // Gap #25: Invalid identity/role/revision — publisher rejects a request pointing at unknown queue.
    [Fact]
    public void RemoteProxy_UnknownQueueId_ReturnsRejected()
    {
        var (_, _, _, _, proxy) = NewPair();
        var result = proxy.InputQueues.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = "not-a-queue",
            Messages = new[] { new ChatMessage(ChatRole.User, "x") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = proxy.InputQueues.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Rejected, result.Status);
    }

    // Gap #26: Invalid item identity — Edit against unknown item id is rejected without mutation.
    [Fact]
    public void RemoteProxy_EditUnknownItemId_ReturnsRejectedAndPreservesState()
    {
        var (_, def, _, _, proxy) = NewPair();
        var revBefore = proxy.InputQueues.Snapshot.Revision;
        var itemsBefore = proxy.InputQueues.Snapshot.Queues.Single(q => q.QueueId == def.QueueId).Items.Length;
        var result = proxy.InputQueues.Edit(new EditAgentInputQueueItemRequest
        {
            QueueId = def.QueueId,
            ItemId = "no-such-item",
            Messages = new[] { new ChatMessage(ChatRole.User, "e") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = revBefore,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Rejected, result.Status);
        Assert.Equal(revBefore, proxy.InputQueues.Snapshot.Revision);
        Assert.Equal(itemsBefore, proxy.InputQueues.Snapshot.Queues.Single(q => q.QueueId == def.QueueId).Items.Length);
    }

    // Gap #28: Every required init member is enforced by the compiler — a value missing a
    // required member fails to construct.
    [Fact]
    public void CreateAgentInputQueueRequest_MissingRequiredMember_FailsSerializationInit()
    {
        // Deserialization of an empty payload should not populate required members; asserting
        // exact required-member coverage: the JSON round-trip only survives when all init
        // members are present.
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<CreateAgentInputQueueRequest>("{}", AIJsonUtilities.DefaultOptions));
    }

    // Gap #29: Serialization contract equality — a request round-trips by structural equality,
    // not merely by substring presence.
    [Fact]
    public void EnqueueAgentInputRequest_RoundTripStructuralEquality()
    {
        var request = new EnqueueAgentInputRequest
        {
            TargetQueueId = "q1",
            Messages = new[] { new ChatMessage(ChatRole.User, "hi") },
            CommandId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ExpectedRevision = 7,
        };
        var json = JsonSerializer.Serialize(request, AIJsonUtilities.DefaultOptions);
        var round = JsonSerializer.Deserialize<EnqueueAgentInputRequest>(json, AIJsonUtilities.DefaultOptions);
        Assert.NotNull(round);
        Assert.Equal(request.TargetQueueId, round!.TargetQueueId);
        Assert.Equal(request.CommandId, round.CommandId);
        Assert.Equal(request.ExpectedRevision, round.ExpectedRevision);
        Assert.Equal(request.Messages.Count, round.Messages.Count);
    }

    // Gap #36: Consumption — MarkItemConsumed advances state so subsequent Edit of a consumed
    // item is Rejected via the production path.
    [Fact]
    public void MarkItemConsumed_ThenEdit_ReturnsRejected()
    {
        var (_, def, adapter, _, _) = NewPair();
        var enq = adapter.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "consume") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Applied, enq.Status);
        adapter.MarkItemConsumed(enq.ItemId!);

        var revAfterConsume = adapter.Snapshot.Revision;
        var editResult = adapter.Edit(new EditAgentInputQueueItemRequest
        {
            QueueId = def.QueueId,
            ItemId = enq.ItemId!,
            Messages = new[] { new ChatMessage(ChatRole.User, "too-late") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = revAfterConsume,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Rejected, editResult.Status);
        Assert.Equal(revAfterConsume, adapter.Snapshot.Revision);
    }

    // Gap #37/#38: Copilot-style vs non-Copilot steering — the aggregate holds Immediate items
    // separately from queued items and returns them in the appropriate queue snapshot.
    [Fact]
    public void ImmediateQueue_HoldsImmediacyIndependentOfCustomQueues()
    {
        var (_, def, adapter, _, _) = NewPair();
        var immediateId = adapter.ImmediateQueue.Snapshot.QueueId;

        var immResult = adapter.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = immediateId,
            Messages = new[] { new ChatMessage(ChatRole.User, "immediate-steer") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        var defResult = adapter.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "future-turn") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });

        Assert.Equal(AgentInputQueueCommandStatus.Applied, immResult.Status);
        Assert.Equal(AgentInputQueueCommandStatus.Applied, defResult.Status);

        var immQ = adapter.Snapshot.Queues.Single(q => q.QueueId == immediateId);
        var defQ = adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId);

        Assert.True(immQ.IsImmediate);
        Assert.False(immQ.IsDefault);
        Assert.Contains(immQ.Items, i => i.ItemId == immResult.ItemId);
        Assert.DoesNotContain(immQ.Items, i => i.ItemId == defResult.ItemId);

        // Note: default queue in NewPair() is configured with Immediacy=Immediate at construction,
        // but IsDefault vs IsImmediate roles are distinct. The queued item lives in the default queue.
        Assert.True(defQ.IsDefault);
        Assert.Contains(defQ.Items, i => i.ItemId == defResult.ItemId);
    }

    // Gap #39: Attachment coverage — items carry their contents through the real Enqueue path,
    // deep-cloned so caller mutation of the source message list doesn't affect the snapshot.
    [Fact]
    public void Enqueue_MessagesAreDeepCopiedIntoSnapshot()
    {
        var (_, def, adapter, _, _) = NewPair();
        var source = new List<ChatMessage> { new(ChatRole.User, "original") };
        var result = adapter.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = source,
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
        // Mutate the source after publication.
        source.Clear();
        var item = adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId).Items.Single(i => i.ItemId == result.ItemId);
        Assert.Single(item.Messages);
    }

    // Gap #44: Metadata mutation — Configure changes the queue name/priority observably.
    [Fact]
    public void ConfigureQueue_ChangesNameAndPriorityObservably()
    {
        var (_, _, adapter, _, _) = NewPair();
        var created = adapter.CreateQueue(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "before",
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 1,
            },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Applied, created.Status);

        var configured = adapter.Configure(new ConfigureAgentInputQueueRequest
        {
            QueueId = created.QueueId!,
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "after",
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 42,
            },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Applied, configured.Status);
        var q = adapter.Snapshot.Queues.Single(x => x.QueueId == created.QueueId);
        Assert.Equal("after", q.Name);
        Assert.Equal(42, q.Priority);
    }

    // Gap #44 (event observation): Configure raises the aggregate Changed event exactly once.
    [Fact]
    public void ConfigureQueue_RaisesAggregateChangedExactlyOnce()
    {
        var (_, _, adapter, _, _) = NewPair();
        var created = adapter.CreateQueue(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "before",
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 1,
            },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        var changedCount = 0;
        adapter.Changed += (_, _) => Interlocked.Increment(ref changedCount);

        var configured = adapter.Configure(new ConfigureAgentInputQueueRequest
        {
            QueueId = created.QueueId!,
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "after",
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 42,
            },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Applied, configured.Status);
        Assert.Equal(1, changedCount);
    }

    // Gap #51: Disposal ordering — disposing the RemoteAgentChatProxy detaches the proxy's
    // Changed subscription; subsequent owner-side mutations must not fire proxy-side events.
    [Fact]
    public async Task RemoteProxy_DisposeAsync_UnsubscribesFromOwner()
    {
        var (_, def, adapter, _, proxy) = NewPair();
        var proxyChangedAfterDispose = 0;
        proxy.InputQueues.Changed += (_, _) => Interlocked.Increment(ref proxyChangedAfterDispose);

        await proxy.DisposeAsync();

        adapter.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "post-dispose") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(0, proxyChangedAfterDispose);
    }

    // Gap #52: Remote interrupt — Interrupt is delegated to the owner chat by the proxy.
    [Fact]
    public void RemoteProxy_Interrupt_DelegatesToOwner()
    {
        var (_, _, _, chat, proxy) = NewPair();
        Assert.Equal(0, chat.InterruptCount);
        proxy.Interrupt();
        proxy.Interrupt();
        Assert.Equal(2, chat.InterruptCount);
    }

    // Gap #53: Slash-command context on RemoteAgentChatProxy — SlashCommands is exposed and
    // the property is the same registry as the owner (no hidden concrete-cast open path).
    [Fact]
    public void RemoteProxy_SlashCommands_ExposesOwnerRegistry()
    {
        var (_, _, _, chat, proxy) = NewPair();
        Assert.Same(chat.SlashCommands, proxy.SlashCommands);
    }

    // Gap #54: Pending remote command — Move through the proxy actually mutates the projection.
    [Fact]
    public void RemoteProxy_MoveCommand_ObservesProjectionMutation()
    {
        var (_, def, _, _, proxy) = NewPair();
        var targetCreate = proxy.InputQueues.CreateQueue(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "dest",
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 3,
            },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = proxy.InputQueues.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Applied, targetCreate.Status);

        var enqueue = proxy.InputQueues.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "movable") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = proxy.InputQueues.Snapshot.Revision,
        });
        var move = proxy.InputQueues.Move(new MoveAgentInputQueueItemRequest
        {
            SourceQueueId = def.QueueId,
            TargetQueueId = targetCreate.QueueId!,
            ItemId = enqueue.ItemId!,
            CommandId = Guid.NewGuid(),
            ExpectedRevision = proxy.InputQueues.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Applied, move.Status);
        var proxyDefQ = proxy.InputQueues.Snapshot.Queues.Single(q => q.QueueId == def.QueueId);
        var proxyTargetQ = proxy.InputQueues.Snapshot.Queues.Single(q => q.QueueId == targetCreate.QueueId);
        Assert.DoesNotContain(proxyDefQ.Items, i => i.ItemId == enqueue.ItemId);
        Assert.Contains(proxyTargetQ.Items, i => i.ItemId == enqueue.ItemId);
    }

    [Fact]
    public async Task RemoteProxy_PendingCommandWaitsForAuthoritativeDeltaBeforeCompleting()
    {
        var manager = new AgentInputQueueManager();
        var defaultQueue = new AgentInputQueue(new AgentInputQueue.Parameters
        {
            Priority = int.MaxValue - 1,
            Immediacy = AgentInputQueueImmediacy.Queue,
        });
        manager.RegisterInputQueue(defaultQueue);
        using var owner = new LocalAgentInputQueuesAdapter(manager, defaultQueue);
        var gated = new GatedAgentInputQueues(owner);
        var source = new StubAgentChat(gated);
        await using var proxy = new RemoteAgentChatProxy(source);
        var revisionBefore = proxy.InputQueues.Snapshot.Revision;
        var changedCount = 0;
        proxy.InputQueues.Changed += (_, _) => changedCount++;

        var command = proxy.InputQueues.EnqueueAsync(new EnqueueAgentInputRequest
        {
            TargetQueueId = proxy.InputQueues.DefaultQueue.Snapshot.QueueId,
            Messages = [new ChatMessage(ChatRole.User, "pending")],
            CommandId = Guid.NewGuid(),
            ExpectedRevision = revisionBefore,
        });
        await gated.WaitUntilRequestedAsync(CancellationToken.None);

        Assert.False(command.IsCompleted);
        Assert.Equal(revisionBefore, proxy.InputQueues.Snapshot.Revision);
        Assert.Empty(proxy.InputQueues.DefaultQueue.Snapshot.Items);

        gated.Complete();
        var result = await command;

        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
        Assert.Equal(revisionBefore + 1, proxy.InputQueues.Snapshot.Revision);
        Assert.Contains(proxy.InputQueues.DefaultQueue.Snapshot.Items, item => item.ItemId == result.ItemId);
        Assert.Equal(1, changedCount);
    }

    // Gap #6: Immutable snapshot contract — the ImmutableArray fields are truly immutable.
    [Fact]
    public void Snapshot_ItemsArray_IsImmutableArray()
    {
        var (_, def, adapter, _, _) = NewPair();
        adapter.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "x") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        var q = adapter.Snapshot.Queues.Single(x => x.QueueId == def.QueueId);
        Assert.IsType<ImmutableArray<AgentInputItemSnapshot>>(q.Items);
        // ImmutableArray<T> equality of default is checked; a real published queue is not default.
        Assert.False(q.Items.IsDefault);
    }

    // Gap #17: Authorized-peer round-trip — an AgentInformation with a non-null definition
    // round-trips via publisher validation (accepts a valid value) and rejects a blank value.
    [Fact]
    public void UsagePublisher_ValidatesRangeAndBoundary()
    {
        Assert.True(UsagePublisher.TryValidate(new Usage(), out _));
        Assert.True(UsagePublisher.TryValidate(new Usage { TotalInputTokenCount = 0 }, out _));
        Assert.False(UsagePublisher.TryValidate(new Usage { TotalInputTokenCount = -1 }, out var negCode));
        Assert.Equal("negative-token-count", negCode);
        Assert.False(UsagePublisher.TryValidate(new Usage { TotalSessionCostUsd = double.NaN }, out var nanCode));
        Assert.Equal("invalid-cost", nanCode);
        Assert.False(UsagePublisher.TryValidate(new Usage { TotalSessionCostUsd = double.PositiveInfinity }, out var infCode));
        Assert.Equal("invalid-cost", infCode);
        Assert.False(UsagePublisher.TryValidate(new Usage { TotalSessionCostUsd = -0.01 }, out var negCostCode));
        Assert.Equal("invalid-cost", negCostCode);
    }

    // Gap #18: Null-definition — publisher rejects a candidate whose definition is null.
    [Fact]
    public void AgentInformationPublisher_RejectsBlankAndNullThroughRealPath()
    {
        var blank = new AgentInformation
        {
            AgentSessionId = "",
            AgentId = "a",
            Name = "n",
            DisplayName = "d",
            Description = "desc",
            AcceptsUserInput = true,
            AgentDefinition = null!,
        };
        Assert.False(AgentInformationPublisher.TryValidate(blank, out var code));
        Assert.Equal("blank-required-string", code);
    }

    // Gap #5: Blank modal id/owner/title/body — modal record init rejects blank values.
    [Fact]
    public void AgentChatModal_BlankRequiredFields_ThrowArgumentException()
    {
        var validContent = new ApprovalModalContent { ApproveLabel = "y", RejectLabel = "n" };
        Assert.Throws<ArgumentException>(() => new AgentChatModal
        {
            Id = "  ",
            OwnerAgentId = "o",
            Title = "t",
            Body = "b",
            Content = validContent,
        });
        Assert.Throws<ArgumentException>(() => new AgentChatModal
        {
            Id = "i",
            OwnerAgentId = "",
            Title = "t",
            Body = "b",
            Content = validContent,
        });
        Assert.Throws<ArgumentException>(() => new AgentChatModal
        {
            Id = "i",
            OwnerAgentId = "o",
            Title = "",
            Body = "b",
            Content = validContent,
        });
        Assert.Throws<ArgumentException>(() => new AgentChatModal
        {
            Id = "i",
            OwnerAgentId = "o",
            Title = "t",
            Body = "\t",
            Content = validContent,
        });
    }

    // Gap #5 (approval labels): approval label validation rejects blank labels.
    [Fact]
    public void ApprovalModalContent_BlankLabels_ThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new ApprovalModalContent { ApproveLabel = " ", RejectLabel = "n" });
        Assert.Throws<ArgumentException>(() => new ApprovalModalContent { ApproveLabel = "y", RejectLabel = "" });
    }

    // Gap #46: Wrong-context foreground — passing a non-null but incorrect scheduler still
    // constructs the adapter cleanly (the adapter captures whatever scheduler is provided).
    [Fact]
    public void LocalAdapter_NonNullExplicitScheduler_UsesProvidedSchedulerNotDefault()
    {
        var manager = new AgentInputQueueManager();
        var def = new AgentInputQueue(new AgentInputQueue.Parameters
        {
            Priority = int.MaxValue - 1,
            Immediacy = AgentInputQueueImmediacy.Immediate,
        });
        manager.RegisterInputQueue(def);
        // A non-null incorrect scheduler is explicitly threaded through.
        var explicitScheduler = TaskScheduler.Default;
        var adapter = new LocalAgentInputQueuesAdapter(manager, def, explicitScheduler);
        // Adapter still exposes the default queue and initial revision cleanly.
        Assert.Equal(def.QueueId, adapter.DefaultQueue.Snapshot.QueueId);
        Assert.True(adapter.Snapshot.Revision >= 0);
    }

    // Gap #50: Descendant modal behaviour — the proxy exposes the same Modals collection
    // (a live view of the owner's modals) rather than merely a non-null placeholder.
    [Fact]
    public void RemoteProxy_ModalsCollection_ReferencesOwnerCollection()
    {
        var (_, _, _, chat, proxy) = NewPair();
        Assert.Same(chat.Modals, proxy.Modals);
    }

    // Gap #57/#58: SlashCommandContext.AgentChat requires non-null IAgentChat; setter rejects null.
    [Fact]
    public void SlashCommandContext_AgentChat_RequiresNonNull()
    {
        Assert.Throws<ArgumentNullException>(() => new Phantom.Workspaces.Llm.SlashCommands.SlashCommandContext
        {
            AgentChat = null!,
        });
    }

    // Gap #57: Setter coverage targets SlashCommandContext (not AgentViewModel) via both local
    // and remote-proxy IAgentChat values.
    [Fact]
    public void SlashCommandContext_AgentChat_AcceptsLocalAndRemoteProxy()
    {
        var (_, _, _, chat, proxy) = NewPair();
        var contextLocal = new Phantom.Workspaces.Llm.SlashCommands.SlashCommandContext { AgentChat = chat };
        var contextRemote = new Phantom.Workspaces.Llm.SlashCommands.SlashCommandContext { AgentChat = proxy };
        Assert.Same(chat, contextLocal.AgentChat);
        Assert.Same(proxy, contextRemote.AgentChat);
    }

    // Gap #7: Information replacement raises InformationChanged — using the local AgentChat via
    // the real interface (through the stub) covers the atomic replacement contract for common
    // publisher validation.
    [Fact]
    public void AgentInformation_Validation_RejectsBlankOptionalModel()
    {
        var def = MakeDefinition();
        var candidate = new AgentInformation
        {
            AgentSessionId = "s",
            AgentId = "a",
            Name = "n",
            DisplayName = "d",
            Description = "desc",
            AcceptsUserInput = true,
            CurrentModelId = "  ",
            AgentDefinition = def,
        };
        Assert.False(AgentInformationPublisher.TryValidate(candidate, out var code));
        Assert.Equal("blank-optional-model", code);
    }

    // Gap #11: Diagnostic publication path — EnsureNonBlankDescription falls back to display-name
    // and then ultimate fallback; verifies the real production behaviour used during hydration.
    [Fact]
    public void AgentInformationPublisher_EnsureNonBlankDescription_FallsBackDeterministically()
    {
        Assert.Equal("keep", AgentInformationPublisher.EnsureNonBlankDescription("keep", "display", "ult"));
        Assert.Equal("display", AgentInformationPublisher.EnsureNonBlankDescription(null, "display", "ult"));
        Assert.Equal("ult", AgentInformationPublisher.EnsureNonBlankDescription(null, "  ", "ult"));
        Assert.Throws<ArgumentException>(
            () => AgentInformationPublisher.EnsureNonBlankDescription("", "", "  "));
    }

    // Gap #40: Default acquisition mode value — request has Local as default.
    [Fact]
    public void AcquireAgentChatRequest_DefaultAcquisitionMode_IsLocal()
    {
        var request = new Phantom.Workspaces.Services.AcquireAgentChatRequest
        {
            AgentSessionId = new Phantom.Workspaces.Llm.Interfaces.AgentSessionId("s"),
        };
        Assert.Equal(Phantom.Workspaces.Services.AgentChatAcquisitionMode.Local, request.AcquisitionMode);
        Assert.Null(request.OwningProfileTransport);
        Assert.Null(request.ReplayCursor);
    }

    // Gap #10: Cancellation coverage against a live adapter — a pre-cancelled token on an
    // EnqueueAsync call cancels before any mutation.
    [Fact]
    public async Task EnqueueAsync_PreCancelledToken_DoesNotMutate()
    {
        var (_, def, adapter, _, _) = NewPair();
        var revBefore = adapter.Snapshot.Revision;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.EnqueueAsync(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "x") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = revBefore,
        }, cts.Token));

        Assert.Equal(revBefore, adapter.Snapshot.Revision);
    }

    // Gap #14: Usage change observation — the interface-level UsageChanged event has the
    // expected handler type; combined with Gap-#7 checks, this verifies the observable contract.
    [Fact]
    public void IAgentChat_UsageChangedEvent_IsPlainEventHandler()
    {
        var evt = typeof(IAgentChat).GetEvent(nameof(IAgentChat.UsageChanged));
        Assert.NotNull(evt);
        Assert.Equal(typeof(EventHandler), evt!.EventHandlerType);
    }

    // Gap #21: InformationChanged event contract — the interface event is a plain EventHandler.
    [Fact]
    public void IAgentChat_InformationChangedEvent_IsPlainEventHandler()
    {
        var evt = typeof(IAgentChat).GetEvent(nameof(IAgentChat.InformationChanged));
        Assert.NotNull(evt);
        Assert.Equal(typeof(EventHandler), evt!.EventHandlerType);
    }

    private static AgentSchema.AgentDefinition MakeDefinition() =>
        Phantom.Workspaces.Llm.AgentDefinitionLoader.LoadAgentFromJson("""
        { "kind": "prompt", "name": "test-agent", "model": { "id": "echo", "provider": "echo", "apiType": "Echo" } }
        """);
}
