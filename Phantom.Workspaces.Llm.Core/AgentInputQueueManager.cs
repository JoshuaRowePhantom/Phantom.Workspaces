namespace Phantom.Workspaces.Llm;

public sealed class AgentInputQueueManager
{
    public enum QueueStateChangeKind
    {
        ItemAdded,
        ItemRemoved,
        ConfigurationChanged,
    }

    public sealed record QueuePublishedEventArgs
    {
        public required AgentInputQueue Queue { get; init; }

        public required AgentInputItem Item { get; init; }
    }

    public sealed record QueueStateChangedEventArgs
    {
        public required AgentInputQueue Queue { get; init; }

        public required QueueStateChangeKind ChangeKind { get; init; }
    }

    private readonly object syncLock = new();
    private readonly List<AgentInputQueue> inputQueues;
    private readonly Dictionary<AgentInputQueue, EventHandler> queueConfigurationHandlers = [];
    private readonly Dictionary<string, string> queueNames = new(StringComparer.Ordinal);
    private long aggregateRevision;

    public event EventHandler<QueuePublishedEventArgs>? QueuePublished;
    public event EventHandler<QueueStateChangedEventArgs>? QueueStateChanged;

    public AgentInputQueueManager()
    {
        this.ImmediateQueue = new AgentInputQueue(
            new AgentInputQueue.Parameters
            {
                Priority = int.MaxValue,
                Immediacy = AgentInputQueueImmediacy.Immediate,
                Name = "Immediate Queue",
            });
        this.inputQueues = [this.ImmediateQueue];
        this.queueConfigurationHandlers[this.ImmediateQueue] = this.OnQueueConfigurationChanged;
        this.ImmediateQueue.ConfigurationChanged += this.OnQueueConfigurationChanged;
        this.queueNames[this.ImmediateQueue.QueueId] = "Immediate Queue";
    }

    public AgentInputQueue ImmediateQueue { get; }

    /// <summary>
    /// Monotonically increasing aggregate revision (issue #1485). Bumped once per applied
    /// mutation across all owned queues so cross-queue commands share a deterministic
    /// conflict rule.
    /// </summary>
    public long AggregateRevision => Volatile.Read(ref this.aggregateRevision);

    /// <summary>Bumps the aggregate revision and returns the new value.</summary>
    internal long BumpAggregateRevision() => Interlocked.Increment(ref this.aggregateRevision);

    /// <summary>
    /// #1485: notifies subscribers that a legacy edit/remove path applied a mutation to
    /// <paramref name="queue"/>. Bumps the aggregate revision and raises
    /// <see cref="QueueStateChanged"/>. Callers on the legacy <see cref="AgentChatQueueManager"/>
    /// edit/remove paths invoke this so the common queue aggregate stays in lock-step with the
    /// legacy surface.
    /// </summary>
    public void NotifyLegacyMutationApplied(AgentInputQueue queue, QueueStateChangeKind kind)
    {
        ArgumentNullException.ThrowIfNull(queue);
        Interlocked.Increment(ref this.aggregateRevision);
        this.QueueStateChanged?.Invoke(
            this,
            new QueueStateChangedEventArgs
            {
                Queue = queue,
                ChangeKind = kind,
            });
    }

    /// <summary>Records an owner-assigned display name for the given queue id.</summary>
    internal void SetQueueName(string queueId, string name)
    {
        lock (this.syncLock)
        {
            this.queueNames[queueId] = name;
        }
    }

    /// <summary>Returns the associated display name for the given queue id, or an empty string.</summary>
    internal string GetQueueName(string queueId)
    {
        lock (this.syncLock)
        {
            return this.queueNames.TryGetValue(queueId, out var name) ? name : string.Empty;
        }
    }

    public IReadOnlyList<AgentInputQueue> InputQueue
    {
        get
        {
            lock (this.syncLock)
            {
                return this.inputQueues.ToArray();
            }
        }
    }

    public IReadOnlyList<AgentInputItem> Enqueue(
        AgentInputQueue queue,
        IEnumerable<AgentInputItem> items)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(items);

        this.RegisterInputQueue(queue);
        var beforeCount = queue.Items.Count;
        var result = queue.Enqueue(items);
        if (result.Count > beforeCount)
        {
            Interlocked.Increment(ref this.aggregateRevision);
            this.QueueStateChanged?.Invoke(
                this,
                new QueueStateChangedEventArgs
                {
                    Queue = queue,
                    ChangeKind = QueueStateChangeKind.ItemAdded,
                });
        }

        return result;
    }

    public void RegisterInputQueue(
        AgentInputQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        lock (this.syncLock)
        {
            if (!this.inputQueues.Contains(queue))
            {
                this.inputQueues.Add(queue);
                this.queueConfigurationHandlers[queue] = this.OnQueueConfigurationChanged;
                queue.ConfigurationChanged += this.OnQueueConfigurationChanged;
            }
        }
    }

    public bool UnregisterInputQueue(
        AgentInputQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        lock (this.syncLock)
        {
            if (ReferenceEquals(queue, this.ImmediateQueue))
            {
                return false;
            }

            var removed = this.inputQueues.Remove(queue);
            if (removed && this.queueConfigurationHandlers.Remove(queue, out var handler))
            {
                queue.ConfigurationChanged -= handler;
            }

            return removed;
        }
    }

    public bool TryDequeueNextImmediate(out AgentInputItem item)
        => this.TryDequeueNext(includeQueued: false, out item);

    public bool TryDequeueNextImmediateOrQueued(out AgentInputItem item)
        => this.TryDequeueNext(includeQueued: true, out item);

    private bool TryDequeueNext(bool includeQueued, out AgentInputItem item)
    {
        AgentInputQueue? selectedQueue;
        lock (this.syncLock)
        {
            selectedQueue = this.inputQueues
                .Where(queue => queue.Items.Count > 0)
                .Where(queue => queue.Immediacy == AgentInputQueueImmediacy.Immediate
                    || (includeQueued && queue.Immediacy == AgentInputQueueImmediacy.Queue))
                .OrderByDescending(queue => queue.Priority)
                .FirstOrDefault();
        }

        if (selectedQueue is null || !this.TryDequeueFirst(selectedQueue, out item))
        {
            item = default!;
            return false;
        }

        this.QueuePublished?.Invoke(
            this,
            new QueuePublishedEventArgs
            {
                Queue = selectedQueue,
                Item = item,
            });

        return true;
    }

    private bool TryDequeueFirst(AgentInputQueue queue, out AgentInputItem item)
    {
        while (true)
        {
            var expected = queue.Items;
            if (expected.Count == 0)
            {
                item = default!;
                return false;
            }

            item = expected[0];
            if (queue.TryRemoveAt(ref expected, 0))
            {
                Interlocked.Increment(ref this.aggregateRevision);
                this.QueueStateChanged?.Invoke(
                    this,
                    new QueueStateChangedEventArgs
                    {
                        Queue = queue,
                        ChangeKind = QueueStateChangeKind.ItemRemoved,
                    });
                return true;
            }
        }
    }

    private void OnQueueConfigurationChanged(object? sender, EventArgs e)
    {
        if (sender is not AgentInputQueue queue)
        {
            return;
        }

        Interlocked.Increment(ref this.aggregateRevision);
        this.QueueStateChanged?.Invoke(
            this,
            new QueueStateChangedEventArgs
            {
                Queue = queue,
                ChangeKind = QueueStateChangeKind.ConfigurationChanged,
            });
    }
}
