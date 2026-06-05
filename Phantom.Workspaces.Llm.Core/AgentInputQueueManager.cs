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

    public event EventHandler<QueuePublishedEventArgs>? QueuePublished;
    public event EventHandler<QueueStateChangedEventArgs>? QueueStateChanged;

    public AgentInputQueueManager()
    {
        this.ImmediateQueue = new AgentInputQueue(
            new AgentInputQueue.Parameters
            {
                Priority = int.MaxValue,
                Immediacy = AgentInputQueueImmediacy.Immediate,
            });
        this.inputQueues = [this.ImmediateQueue];
        this.queueConfigurationHandlers[this.ImmediateQueue] = this.OnQueueConfigurationChanged;
        this.ImmediateQueue.ConfigurationChanged += this.OnQueueConfigurationChanged;
    }

    public AgentInputQueue ImmediateQueue { get; }

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

        this.QueueStateChanged?.Invoke(
            this,
            new QueueStateChangedEventArgs
            {
                Queue = queue,
                ChangeKind = QueueStateChangeKind.ConfigurationChanged,
            });
    }
}
