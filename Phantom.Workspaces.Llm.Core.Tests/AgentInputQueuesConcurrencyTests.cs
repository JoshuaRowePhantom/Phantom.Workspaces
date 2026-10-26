using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentInputQueuesConcurrencyTests
{
    [Fact]
    public async Task Enqueue_OwnerDequeueDuringCapturedNotification_PublishesCurrentSnapshot()
    {
        var ct = CancellationToken.None;
        var manager = new AgentInputQueueManager();
        var queue = new AgentInputQueue();
        manager.RegisterInputQueue(queue);
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.QueueStateChanged += (_, e) =>
        {
            if (e.ChangeKind == AgentInputQueueManager.QueueStateChangeKind.ItemRemoved)
                removed.TrySetResult();
        };
        using var adapter = new LocalAgentInputQueuesAdapter(manager, queue);
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var gated = 0;
        adapter.DefaultQueue.Changed += (_, _) =>
        {
            if (Interlocked.Exchange(ref gated, 1) == 0)
            {
                captured.SetResult();
                // Only this owned worker is gated, never the UI/test continuation. The
                // coordinator releases it before awaiting dequeue/consumption completion.
                release.Wait(ct);
            }
        };
        var request = Request(queue, adapter.Snapshot.Revision, "first");
        var command = Task.Run(() => adapter.Enqueue(request), ct);
        Task<bool>? dequeue = null;
        try
        {
            await captured.Task.WaitAsync(ct);
            dequeue = Task.Run(() => manager.TryDequeueNextImmediateOrQueued(out _), ct);
            await removed.Task.WaitAsync(ct);
        }
        finally
        {
            release.Set();
        }
        var applied = await command.WaitAsync(ct);
        Assert.True(await dequeue!.WaitAsync(ct));
        Assert.Equal(AgentInputQueueCommandStatus.Applied, applied.Status);
        Assert.Equal(applied.Revision, applied.CurrentSnapshot!.Value.Revision);
        Assert.Equal(manager.AggregateRevision, adapter.Snapshot.Revision);
        Assert.Empty(adapter.Snapshot.Queues.Single(q => q.IsDefault).Items);
        Assert.Equal(AgentInputQueueCommandStatus.Applied,
            adapter.Enqueue(Request(queue, adapter.Snapshot.Revision, "next")).Status);
    }

    [Fact]
    public void Enqueue_ObserverDequeuesAfterCapture_PublishesRemovalAndAcceptsNextCommand()
    {
        var (manager, queue, adapter) = CreateAdapter();
        using (adapter)
        {
            var published = new List<AgentInputQueuesSnapshot>();
            adapter.Changed += (_, _) => published.Add(adapter.Snapshot);
            var consumed = false;
            adapter.DefaultQueue.Changed += (_, _) =>
            {
                if (!consumed && queue.Items.Count != 0)
                {
                    consumed = true;
                    Assert.True(manager.TryDequeueNextImmediateOrQueued(out _));
                }
            };

            var first = adapter.Enqueue(Request(queue, adapter.Snapshot.Revision, "first"));

            Assert.Equal(AgentInputQueueCommandStatus.Applied, first.Status);
            Assert.Equal(first.Revision, first.CurrentSnapshot!.Value.Revision);
            Assert.Equal(manager.AggregateRevision, adapter.Snapshot.Revision);
            Assert.Empty(adapter.Snapshot.Queues.Single(q => q.IsDefault).Items);
            Assert.Equal(manager.AggregateRevision, published[^1].Revision);
            Assert.True(published.Zip(published.Skip(1)).All(pair => pair.First.Revision < pair.Second.Revision));

            var second = adapter.Enqueue(Request(queue, published[^1].Revision, "second"));
            Assert.Equal(AgentInputQueueCommandStatus.Applied, second.Status);
            Assert.Equal(second.ItemId, Assert.Single(queue.Items).ItemId);
        }
    }

    [Fact]
    public void Enqueue_ObserverDequeues_StaleConflictContainsMatchingRevisionAndContents()
    {
        var (manager, queue, adapter) = CreateAdapter();
        using (adapter)
        {
            var consumed = false;
            adapter.DefaultQueue.Changed += (_, _) =>
            {
                if (!consumed)
                {
                    consumed = true;
                    Assert.True(manager.TryDequeueNextImmediateOrQueued(out _));
                }
            };
            var first = adapter.Enqueue(Request(queue, adapter.Snapshot.Revision, "first"));

            var stale = adapter.Enqueue(Request(queue, first.Revision, "must conflict"));

            Assert.Equal(AgentInputQueueCommandStatus.Conflict, stale.Status);
            Assert.Equal(manager.AggregateRevision, stale.Revision);
            Assert.Equal(stale.Revision, stale.CurrentSnapshot!.Value.Revision);
            Assert.Empty(stale.CurrentSnapshot.Value.Queues.Single(q => q.IsDefault).Items);
            Assert.Empty(queue.Items);
        }
    }

    [Fact]
    public void Enqueue_QueueObserverReplaysCommand_DoesNotApplyTwice()
    {
        var (_, queue, adapter) = CreateAdapter();
        using (adapter)
        {
            var request = Request(queue, adapter.Snapshot.Revision, "once");
            AgentInputQueueCommandResult? replay = null;
            var replaying = false;
            adapter.DefaultQueue.Changed += (_, _) =>
            {
                if (!replaying)
                {
                    replaying = true;
                    replay = adapter.Enqueue(request);
                }
            };

            var first = adapter.Enqueue(request);

            Assert.Equal(AgentInputQueueCommandStatus.Applied, first.Status);
            Assert.Equal(AgentInputQueueCommandStatus.Duplicate, replay!.Value.Status);
            Assert.Equal(first.ItemId, replay.Value.ItemId);
            Assert.Single(queue.Items);
        }
    }

    [Fact]
    public void Enqueue_QueuedForegroundNotifications_CoalescesToOneCurrentRevision()
    {
        var scheduler = new QueuedScheduler();
        var (_, queue, adapter) = CreateAdapter(scheduler);
        using (adapter)
        {
            var revisions = new List<long>();
            adapter.Changed += (_, _) => revisions.Add(adapter.Snapshot.Revision);

            adapter.Enqueue(Request(queue, adapter.Snapshot.Revision, "one"));
            adapter.Enqueue(Request(queue, adapter.Snapshot.Revision, "two"));
            scheduler.Drain();

            Assert.Equal([adapter.Snapshot.Revision], revisions);
            Assert.Equal(2, queue.Items.Count);
        }
    }

    [Fact]
    public void Move_SynchronousSteeringConsumesTarget_ResultAndPublicationIncludeRemoval()
    {
        var (manager, queue, adapter) = CreateAdapter();
        using (adapter)
        {
            var enqueue = adapter.Enqueue(Request(queue, adapter.Snapshot.Revision, "steer"));
            var removals = 0;
            manager.QueueStateChanged += (sender, e) =>
            {
                if (e.ChangeKind == AgentInputQueueManager.QueueStateChangeKind.ItemAdded
                    && ReferenceEquals(e.Queue, manager.ImmediateQueue))
                {
                    Assert.True(manager.TryDequeueNextImmediate(out _));
                    removals++;
                }
            };
            var published = new List<AgentInputQueuesSnapshot>();
            adapter.Changed += (_, args) =>
                published.Add(Assert.IsType<AgentInputQueuesChangedEventArgs>(args).Snapshot);

            var moved = adapter.Move(new MoveAgentInputQueueItemRequest
            {
                SourceQueueId = queue.QueueId,
                TargetQueueId = manager.ImmediateQueue.QueueId,
                ItemId = enqueue.ItemId!,
                CommandId = Guid.NewGuid(),
                ExpectedRevision = adapter.Snapshot.Revision,
            });

            Assert.Equal(1, removals);
            Assert.Equal(AgentInputQueueCommandStatus.Applied, moved.Status);
            Assert.Equal(manager.AggregateRevision, moved.Revision);
            Assert.Equal(moved.Revision, moved.CurrentSnapshot!.Value.Revision);
            Assert.All(moved.CurrentSnapshot.Value.Queues, q => Assert.Empty(q.Items));
            Assert.Equal(moved.CurrentSnapshot.Value, Assert.Single(published));
            var edit = adapter.Edit(new EditAgentInputQueueItemRequest
            {
                QueueId = manager.ImmediateQueue.QueueId,
                ItemId = enqueue.ItemId!,
                Messages = [new ChatMessage(ChatRole.User, "too late")],
                CommandId = Guid.NewGuid(),
                ExpectedRevision = moved.Revision,
            });
            Assert.Equal(AgentInputQueueErrorCodes.ItemAlreadyConsumed, edit.ErrorCode);
        }
    }

    [Fact]
    public void AggregateObserverMutatesAgain_EventSnapshotsRemainOrderedAndImmutable()
    {
        var (_, queue, adapter) = CreateAdapter();
        using (adapter)
        {
            var reentered = false;
            adapter.Changed += (_, _) =>
            {
                if (!reentered)
                {
                    reentered = true;
                    adapter.Enqueue(Request(queue, adapter.Snapshot.Revision, "second"));
                }
            };
            var published = new List<AgentInputQueuesSnapshot>();
            adapter.Changed += (_, args) =>
                published.Add(Assert.IsType<AgentInputQueuesChangedEventArgs>(args).Snapshot);

            adapter.Enqueue(Request(queue, adapter.Snapshot.Revision, "first"));

            Assert.Equal(2, published.Count);
            Assert.True(published[0].Revision < published[1].Revision);
            Assert.Single(published[0].Queues.Single(q => q.IsDefault).Items);
            Assert.Equal(2, published[1].Queues.Single(q => q.IsDefault).Items.Length);
        }
    }

    [Fact]
    public void ManagerQueueRegistrationAndRemoval_PublishAdvancingMembershipSnapshots()
    {
        var (manager, _, adapter) = CreateAdapter();
        using (adapter)
        {
            var initial = adapter.Snapshot.Revision;
            var published = new List<AgentInputQueuesSnapshot>();
            adapter.Changed += (_, args) =>
                published.Add(Assert.IsType<AgentInputQueuesChangedEventArgs>(args).Snapshot);
            var added = new AgentInputQueue();

            manager.RegisterInputQueue(added);
            manager.RegisterInputQueue(added);
            Assert.True(manager.UnregisterInputQueue(added));
            Assert.False(manager.UnregisterInputQueue(added));
            Assert.False(manager.UnregisterInputQueue(manager.ImmediateQueue));

            Assert.Equal(2, published.Count);
            Assert.True(initial < published[0].Revision);
            Assert.True(published[0].Revision < published[1].Revision);
            Assert.Contains(published[0].Queues, q => q.QueueId == added.QueueId);
            Assert.DoesNotContain(published[1].Queues, q => q.QueueId == added.QueueId);
        }
    }

    [Fact]
    public void RegisteredRawConfigure_PublishesOneCompleteConfiguration()
    {
        var (manager, queue, adapter) = CreateAdapter();
        using (adapter)
        {
            var published = new List<AgentInputQueuesSnapshot>();
            adapter.Changed += (_, args) =>
                published.Add(Assert.IsType<AgentInputQueuesChangedEventArgs>(args).Snapshot);

            queue.Configure(new AgentInputQueue.Parameters
            {
                Priority = 19,
                Immediacy = AgentInputQueueImmediacy.Held,
                CoalescingKey = "changed",
            });

            var current = Assert.Single(published);
            Assert.Equal(manager.AggregateRevision, current.Revision);
            var configured = current.Queues.Single(q => q.IsDefault);
            Assert.Equal(19, configured.Priority);
            Assert.Equal(AgentInputQueueImmediacy.Held, configured.Immediacy);
            Assert.Equal("changed", configured.CoalescingKey);
            Assert.Equal(queue.Revision, configured.Revision);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyRemoval_PublishesCurrentEmptySnapshot(bool clear)
    {
        var manager = new AgentInputQueueManager();
        var legacy = new AgentChatQueueManager(manager);
        using var adapter = new LocalAgentInputQueuesAdapter(manager, legacy.DefaultInputQueue.Queue);
        var queue = legacy.DefaultInputQueue.Queue;
        adapter.Enqueue(Request(queue, adapter.Snapshot.Revision, "remove"));
        var previous = adapter.Snapshot.Revision;
        var published = new List<AgentInputQueuesSnapshot>();
        adapter.Changed += (_, args) =>
            published.Add(Assert.IsType<AgentInputQueuesChangedEventArgs>(args).Snapshot);

        if (clear)
            legacy.ClearQueue(legacy.DefaultInputQueue);
        else
            Assert.True(legacy.RemoveQueueItem(legacy.DefaultInputQueue, Assert.Single(queue.Items)));

        var current = Assert.Single(published);
        Assert.True(previous < current.Revision);
        Assert.Equal(manager.AggregateRevision, current.Revision);
        Assert.Empty(current.Queues.Single(q => q.IsDefault).Items);
        Assert.Equal(AgentInputQueueCommandStatus.Applied,
            adapter.Enqueue(Request(queue, current.Revision, "next")).Status);
    }

    [Fact]
    public void Dispose_WithQueuedNotification_UnsubscribesAndSuppressesPendingCallbacks()
    {
        var scheduler = new QueuedScheduler();
        var (manager, queue, adapter) = CreateAdapter(scheduler);
        var changed = 0;
        adapter.Changed += (_, _) => changed++;
        adapter.DefaultQueue.Changed += (_, _) => changed++;
        adapter.Enqueue(Request(queue, adapter.Snapshot.Revision, "pending"));
        var snapshot = adapter.Snapshot;

        adapter.Dispose();
        adapter.Dispose();
        manager.Enqueue(queue, [new AgentInputItem { Messages = [new ChatMessage(ChatRole.User, "later")] }]);
        scheduler.Drain();

        Assert.Equal(0, changed);
        Assert.Equal(snapshot, adapter.Snapshot);
        Assert.Throws<ObjectDisposedException>(() => adapter.Enqueue(Request(queue, snapshot.Revision, "disposed")));
    }

    private static (AgentInputQueueManager Manager, AgentInputQueue Queue, LocalAgentInputQueuesAdapter Adapter)
        CreateAdapter(TaskScheduler? scheduler = null)
    {
        var manager = new AgentInputQueueManager();
        var queue = new AgentInputQueue();
        manager.RegisterInputQueue(queue);
        return (manager, queue, new LocalAgentInputQueuesAdapter(manager, queue, scheduler));
    }

    private static EnqueueAgentInputRequest Request(AgentInputQueue queue, long revision, string text) => new()
    {
        TargetQueueId = queue.QueueId,
        ExpectedRevision = revision,
        CommandId = Guid.NewGuid(),
        Messages = [new ChatMessage(ChatRole.User, text)],
    };

    private sealed class QueuedScheduler : TaskScheduler
    {
        private readonly Queue<Task> tasks = new();

        protected override IEnumerable<Task>? GetScheduledTasks() => this.tasks.ToArray();
        protected override void QueueTask(Task task) => this.tasks.Enqueue(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        internal void Drain()
        {
            while (this.tasks.TryDequeue(out var task))
            {
                Assert.True(this.TryExecuteTask(task));
                Assert.Null(task.Exception);
            }
        }
    }
}
