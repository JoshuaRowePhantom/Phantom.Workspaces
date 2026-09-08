using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Local owner-side implementation of <see cref="IAgentInputQueues"/> (issue #1485).
/// Sits over the existing <see cref="AgentInputQueueManager"/>/<see cref="AgentChatQueueManager"/>
/// domain types and exposes an immutable snapshot/command surface consumed by both the local UI
/// and the remote proxy. Commands are owner-authoritative: successful mutations bump the aggregate
/// revision and refresh the atomically-replaced snapshot before <see cref="IAgentInputQueues.Changed"/>
/// is raised.
/// </summary>
internal sealed class LocalAgentInputQueuesAdapter : IAgentInputQueues, IDisposable
{
    private const string ImmediateQueueDisplayName = "Immediate Queue";
    private const string DefaultQueueDisplayName = "Default Queue";

    private readonly object stateLock = new();
    private readonly AgentInputQueueManager manager;
    private readonly AgentInputQueue defaultQueue;
    private readonly SynchronizationContext? foregroundContext;
    private readonly List<LocalAgentInputQueue> queues = new();
    private readonly Dictionary<string, LocalAgentInputQueue> queuesById = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, (string Payload, AgentInputQueueCommandResult Result)> commandLog = new();
    private readonly HashSet<string> consumedItemIds = new(StringComparer.Ordinal);
    private AgentInputQueuesSnapshot snapshot;
    private int nextUserPriority = 10;
    private bool disposed;
    private bool commandInProgress;

    public event EventHandler? Changed;

    public LocalAgentInputQueuesAdapter(
        AgentInputQueueManager manager,
        AgentInputQueue defaultQueue)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(defaultQueue);
        this.manager = manager;
        this.defaultQueue = defaultQueue;
        // #1485: capture the foreground scheduler once so Changed events fire on the UI thread.
        this.foregroundContext = SynchronizationContext.Current;

        manager.SetQueueName(manager.ImmediateQueue.QueueId, ImmediateQueueDisplayName);
        manager.SetQueueName(defaultQueue.QueueId, DefaultQueueDisplayName);

        var immediate = new LocalAgentInputQueue(this, manager.ImmediateQueue, ImmediateQueueDisplayName, isDefault: false, isImmediate: true);
        var def = new LocalAgentInputQueue(this, defaultQueue, DefaultQueueDisplayName, isDefault: true, isImmediate: false);
        this.queues.Add(immediate);
        this.queues.Add(def);
        this.queuesById[immediate.QueueId] = immediate;
        this.queuesById[def.QueueId] = def;
        this.ImmediateQueue = immediate;
        this.DefaultQueue = def;

        // Track additional queues that the underlying manager already knows about (e.g. user queues
        // created directly on the AgentChatQueueManager).
        foreach (var raw in manager.InputQueue)
        {
            if (this.queuesById.ContainsKey(raw.QueueId))
            {
                continue;
            }

            var displayName = manager.GetQueueName(raw.QueueId);
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = $"Queue {raw.QueueId[..Math.Min(6, raw.QueueId.Length)]}";
                manager.SetQueueName(raw.QueueId, displayName);
            }
            var wrapped = new LocalAgentInputQueue(this, raw, displayName, isDefault: false, isImmediate: false);
            this.queues.Add(wrapped);
            this.queuesById[wrapped.QueueId] = wrapped;
        }

        manager.QueueStateChanged += this.OnManagerQueueStateChanged;
        manager.QueuePublished += this.OnManagerQueuePublished;
        this.snapshot = this.BuildSnapshot();
    }

    public AgentInputQueuesSnapshot Snapshot
    {
        get
        {
            lock (this.stateLock)
            {
                return this.snapshot;
            }
        }
    }

    public IReadOnlyList<IAgentInputQueue> Queues
    {
        get
        {
            lock (this.stateLock)
            {
                return this.queues.Cast<IAgentInputQueue>().ToArray();
            }
        }
    }

    public IAgentInputQueue DefaultQueue { get; }

    public IAgentInputQueue ImmediateQueue { get; }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }
        this.disposed = true;
        this.manager.QueueStateChanged -= this.OnManagerQueueStateChanged;
        this.manager.QueuePublished -= this.OnManagerQueuePublished;
    }

    public Task<AgentInputQueueCommandResult> CreateQueueAsync(
        CreateAgentInputQueueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(this.Execute(request, request.CommandId, request.ExpectedRevision, () =>
        {
            var (ok, err) = ValidateConfiguration(request.Configuration);
            if (!ok)
            {
                return Reject(request.CommandId, err!, queueId: null, itemId: null);
            }

            lock (this.stateLock)
            {
                if (this.queues.Any(q => string.Equals(q.Name, request.Configuration.Name, StringComparison.Ordinal)))
                {
                    return Reject(request.CommandId, AgentInputQueueErrorCodes.DuplicateName, queueId: null, itemId: null);
                }

                var priority = request.Configuration.Priority > 0
                    ? request.Configuration.Priority
                    : this.nextUserPriority++;
                var underlying = new AgentInputQueue(new AgentInputQueue.Parameters
                {
                    Priority = priority,
                    Immediacy = request.Configuration.Immediacy,
                    CoalescingKey = request.Configuration.CoalescingKey,
                });
                this.manager.RegisterInputQueue(underlying);
                this.manager.SetQueueName(underlying.QueueId, request.Configuration.Name);
                var wrapped = new LocalAgentInputQueue(this, underlying, request.Configuration.Name, isDefault: false, isImmediate: false);
                this.queues.Add(wrapped);
                this.queuesById[wrapped.QueueId] = wrapped;

                var revision = this.manager.BumpAggregateRevision();
                this.RefreshSnapshotLocked();
                return new AgentInputQueueCommandResult
                {
                    CommandId = request.CommandId,
                    Status = AgentInputQueueCommandStatus.Applied,
                    QueueId = underlying.QueueId,
                    Revision = revision,
                };
            }
        }));
    }

    public Task<AgentInputQueueCommandResult> DeleteQueueAsync(
        DeleteAgentInputQueueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(this.Execute(request, request.CommandId, request.ExpectedRevision, () =>
        {
            if (string.IsNullOrWhiteSpace(request.QueueId))
            {
                return Reject(request.CommandId, AgentInputQueueErrorCodes.InvalidRequest, queueId: null, itemId: null);
            }

            lock (this.stateLock)
            {
                if (!this.queuesById.TryGetValue(request.QueueId, out var queue))
                {
                    return Reject(request.CommandId, AgentInputQueueErrorCodes.UnknownQueue, queueId: request.QueueId, itemId: null);
                }
                if (queue.IsDefault || queue.IsImmediate)
                {
                    return Reject(request.CommandId, AgentInputQueueErrorCodes.ProtectedQueue, queueId: queue.QueueId, itemId: null);
                }
                if (queue.Underlying.Items.Count != 0)
                {
                    return Reject(request.CommandId, AgentInputQueueErrorCodes.QueueNotEmpty, queueId: queue.QueueId, itemId: null);
                }

                this.manager.UnregisterInputQueue(queue.Underlying);
                this.queues.Remove(queue);
                this.queuesById.Remove(queue.QueueId);

                var revision = this.manager.BumpAggregateRevision();
                this.RefreshSnapshotLocked();
                return new AgentInputQueueCommandResult
                {
                    CommandId = request.CommandId,
                    Status = AgentInputQueueCommandStatus.Applied,
                    QueueId = queue.QueueId,
                    Revision = revision,
                };
            }
        }));
    }

    public Task<AgentInputQueueCommandResult> EnqueueAsync(
        EnqueueAgentInputRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(this.Execute(request, request.CommandId, request.ExpectedRevision, () =>
        {
            if (string.IsNullOrWhiteSpace(request.TargetQueueId))
            {
                return Reject(request.CommandId, AgentInputQueueErrorCodes.InvalidRequest, queueId: null, itemId: null);
            }
            if (request.Messages is null || request.Messages.Count == 0)
            {
                return Reject(request.CommandId, AgentInputQueueErrorCodes.InvalidRequest, queueId: request.TargetQueueId, itemId: null);
            }

            lock (this.stateLock)
            {
                if (!this.queuesById.TryGetValue(request.TargetQueueId, out var queue))
                {
                    return Reject(request.CommandId, AgentInputQueueErrorCodes.UnknownQueue, queueId: request.TargetQueueId, itemId: null);
                }

                var item = new AgentInputItem
                {
                    Messages = CopyMessagesForOwner(request.Messages),
                };
                this.manager.Enqueue(queue.Underlying, [item]);
                var revision = this.manager.AggregateRevision;
                this.RefreshSnapshotLocked();
                return new AgentInputQueueCommandResult
                {
                    CommandId = request.CommandId,
                    Status = AgentInputQueueCommandStatus.Applied,
                    QueueId = queue.QueueId,
                    ItemId = item.ItemId,
                    Revision = revision,
                };
            }
        }));
    }

    public Task<AgentInputQueueCommandResult> EditAsync(
        EditAgentInputQueueItemRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(this.Execute(request, request.CommandId, request.ExpectedRevision, () =>
        {
            if (string.IsNullOrWhiteSpace(request.QueueId) || string.IsNullOrWhiteSpace(request.ItemId))
            {
                return Reject(request.CommandId, AgentInputQueueErrorCodes.InvalidRequest, queueId: null, itemId: null);
            }
            if (request.Messages is null || request.Messages.Count == 0)
            {
                return Reject(request.CommandId, AgentInputQueueErrorCodes.InvalidRequest, queueId: request.QueueId, itemId: request.ItemId);
            }

            lock (this.stateLock)
            {
                if (this.consumedItemIds.Contains(request.ItemId))
                {
                    return Reject(request.CommandId, AgentInputQueueErrorCodes.ItemAlreadyConsumed, queueId: request.QueueId, itemId: request.ItemId);
                }
                if (!this.queuesById.TryGetValue(request.QueueId, out var queue))
                {
                    return Reject(request.CommandId, AgentInputQueueErrorCodes.UnknownQueue, queueId: request.QueueId, itemId: request.ItemId);
                }
                while (true)
                {
                    var expected = queue.Underlying.Items;
                    var index = -1;
                    for (var i = 0; i < expected.Count; i++)
                    {
                        if (string.Equals(expected[i].ItemId, request.ItemId, StringComparison.Ordinal))
                        {
                            index = i;
                            break;
                        }
                    }
                    if (index < 0)
                    {
                        return Reject(request.CommandId, AgentInputQueueErrorCodes.UnknownItem, queueId: request.QueueId, itemId: request.ItemId);
                    }
                    var replacement = expected[index] with { Messages = CopyMessagesForOwner(request.Messages) };
                    if (queue.Underlying.TryUpdateAt(ref expected, index, replacement))
                    {
                        break;
                    }
                }

                var revision = this.manager.BumpAggregateRevision();
                this.RefreshSnapshotLocked();
                return new AgentInputQueueCommandResult
                {
                    CommandId = request.CommandId,
                    Status = AgentInputQueueCommandStatus.Applied,
                    QueueId = queue.QueueId,
                    ItemId = request.ItemId,
                    Revision = revision,
                };
            }
        }));
    }

    public Task<AgentInputQueueCommandResult> RemoveAsync(
        RemoveAgentInputQueueItemRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(this.Execute(request, request.CommandId, request.ExpectedRevision, () =>
        {
            if (string.IsNullOrWhiteSpace(request.QueueId) || string.IsNullOrWhiteSpace(request.ItemId))
            {
                return Reject(request.CommandId, AgentInputQueueErrorCodes.InvalidRequest, queueId: null, itemId: null);
            }
            lock (this.stateLock)
            {
                if (!this.queuesById.TryGetValue(request.QueueId, out var queue))
                {
                    return Reject(request.CommandId, AgentInputQueueErrorCodes.UnknownQueue, queueId: request.QueueId, itemId: request.ItemId);
                }
                while (true)
                {
                    var expected = queue.Underlying.Items;
                    var target = expected.FirstOrDefault(x => string.Equals(x.ItemId, request.ItemId, StringComparison.Ordinal));
                    if (target is null)
                    {
                        return Reject(request.CommandId, AgentInputQueueErrorCodes.UnknownItem, queueId: request.QueueId, itemId: request.ItemId);
                    }
                    if (queue.Underlying.TryRemove(ref expected, target))
                    {
                        break;
                    }
                }
                var revision = this.manager.BumpAggregateRevision();
                this.RefreshSnapshotLocked();
                return new AgentInputQueueCommandResult
                {
                    CommandId = request.CommandId,
                    Status = AgentInputQueueCommandStatus.Applied,
                    QueueId = queue.QueueId,
                    ItemId = request.ItemId,
                    Revision = revision,
                };
            }
        }));
    }

    public Task<AgentInputQueueCommandResult> MoveAsync(
        MoveAgentInputQueueItemRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(this.Execute(request, request.CommandId, request.ExpectedRevision, () =>
        {
            if (string.IsNullOrWhiteSpace(request.SourceQueueId)
                || string.IsNullOrWhiteSpace(request.TargetQueueId)
                || string.IsNullOrWhiteSpace(request.ItemId))
            {
                return Reject(request.CommandId, AgentInputQueueErrorCodes.InvalidRequest, queueId: null, itemId: null);
            }

            lock (this.stateLock)
            {
                if (!this.queuesById.TryGetValue(request.SourceQueueId, out var source))
                {
                    return Reject(request.CommandId, AgentInputQueueErrorCodes.UnknownQueue, queueId: request.SourceQueueId, itemId: request.ItemId);
                }
                if (!this.queuesById.TryGetValue(request.TargetQueueId, out var target))
                {
                    return Reject(request.CommandId, AgentInputQueueErrorCodes.UnknownQueue, queueId: request.TargetQueueId, itemId: request.ItemId);
                }
                if (!string.IsNullOrEmpty(request.BeforeItemId)
                    && (string.Equals(request.BeforeItemId, request.ItemId, StringComparison.Ordinal)
                        || !target.Underlying.Items.Any(
                            item => string.Equals(item.ItemId, request.BeforeItemId, StringComparison.Ordinal))))
                {
                    return Reject(
                        request.CommandId,
                        AgentInputQueueErrorCodes.UnknownItem,
                        queueId: request.TargetQueueId,
                        itemId: request.BeforeItemId);
                }

                // #1485: same-queue reorder is a single atomic Update so per-queue revision only
                // increments once for the whole transaction.
                if (ReferenceEquals(source, target))
                {
                    while (true)
                    {
                        var expected = source.Underlying.Items;
                        var candidate = expected.FirstOrDefault(x => string.Equals(x.ItemId, request.ItemId, StringComparison.Ordinal));
                        if (candidate is null)
                        {
                            return Reject(request.CommandId, AgentInputQueueErrorCodes.UnknownItem, queueId: request.SourceQueueId, itemId: request.ItemId);
                        }
                        var withoutMoved = expected.Remove(candidate);
                        int insertionIndex = withoutMoved.Count;
                        if (!string.IsNullOrEmpty(request.BeforeItemId))
                        {
                            insertionIndex = -1;
                            for (var i = 0; i < withoutMoved.Count; i++)
                            {
                                if (string.Equals(withoutMoved[i].ItemId, request.BeforeItemId, StringComparison.Ordinal))
                                {
                                    insertionIndex = i;
                                    break;
                                }
                            }
                            if (insertionIndex < 0)
                            {
                                return Reject(request.CommandId, AgentInputQueueErrorCodes.UnknownItem, queueId: request.TargetQueueId, itemId: request.BeforeItemId);
                            }
                        }
                        var updated = withoutMoved.Insert(insertionIndex, candidate);
                        if (source.Underlying.Update(ref expected, updated))
                        {
                            break;
                        }
                    }

                    var singleRevision = this.manager.BumpAggregateRevision();
                    this.RefreshSnapshotLocked();
                    return new AgentInputQueueCommandResult
                    {
                        CommandId = request.CommandId,
                        Status = AgentInputQueueCommandStatus.Applied,
                        QueueId = target.QueueId,
                        ItemId = request.ItemId,
                        Revision = singleRevision,
                    };
                }

                AgentInputItem? moved = null;
                while (true)
                {
                    var expected = source.Underlying.Items;
                    var candidate = expected.FirstOrDefault(x => string.Equals(x.ItemId, request.ItemId, StringComparison.Ordinal));
                    if (candidate is null)
                    {
                        return Reject(request.CommandId, AgentInputQueueErrorCodes.UnknownItem, queueId: request.SourceQueueId, itemId: request.ItemId);
                    }
                    if (source.Underlying.TryRemove(ref expected, candidate))
                    {
                        moved = candidate;
                        break;
                    }
                }

                // Insert at the resolved position (append when BeforeItemId is null).
                while (true)
                {
                    var expected = target.Underlying.Items;
                    int insertionIndex = expected.Count;
                    if (!string.IsNullOrEmpty(request.BeforeItemId))
                    {
                        insertionIndex = -1;
                        for (var i = 0; i < expected.Count; i++)
                        {
                            if (string.Equals(expected[i].ItemId, request.BeforeItemId, StringComparison.Ordinal))
                            {
                                insertionIndex = i;
                                break;
                            }
                        }
                    }
                    var updated = expected.Insert(insertionIndex, moved!);
                    if (target.Underlying.Update(ref expected, updated))
                    {
                        break;
                    }
                }

                var revision = this.manager.BumpAggregateRevision();
                this.RefreshSnapshotLocked();
                return new AgentInputQueueCommandResult
                {
                    CommandId = request.CommandId,
                    Status = AgentInputQueueCommandStatus.Applied,
                    QueueId = target.QueueId,
                    ItemId = request.ItemId,
                    Revision = revision,
                };
            }
        }));
    }

    public Task<AgentInputQueueCommandResult> ConfigureAsync(
        ConfigureAgentInputQueueRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(this.Execute(request, request.CommandId, request.ExpectedRevision, () =>
        {
            if (string.IsNullOrWhiteSpace(request.QueueId))
            {
                return Reject(request.CommandId, AgentInputQueueErrorCodes.InvalidRequest, queueId: null, itemId: null);
            }
            var (ok, err) = ValidateConfiguration(request.Configuration);
            if (!ok)
            {
                return Reject(request.CommandId, err!, queueId: request.QueueId, itemId: null);
            }
            lock (this.stateLock)
            {
                if (!this.queuesById.TryGetValue(request.QueueId, out var queue))
                {
                    return Reject(request.CommandId, AgentInputQueueErrorCodes.UnknownQueue, queueId: request.QueueId, itemId: null);
                }
                if (queue.IsImmediate && request.Configuration.Immediacy != AgentInputQueueImmediacy.Immediate)
                {
                    return Reject(request.CommandId, AgentInputQueueErrorCodes.FixedRoleConfiguration, queueId: queue.QueueId, itemId: null);
                }
                if (queue.IsDefault && request.Configuration.Immediacy == AgentInputQueueImmediacy.Immediate
                    && queue.Underlying.Immediacy != AgentInputQueueImmediacy.Immediate)
                {
                    // Default queue is allowed Immediate but not other fixed-role transitions.
                }

                queue.Rename(request.Configuration.Name);
                this.manager.SetQueueName(queue.QueueId, request.Configuration.Name);
                queue.Underlying.Configure(new AgentInputQueue.Parameters
                {
                    Priority = request.Configuration.Priority,
                    Immediacy = request.Configuration.Immediacy,
                    CoalescingKey = request.Configuration.CoalescingKey,
                });

                // manager.OnQueueConfigurationChanged already bumped aggregate revision; read it back.
                var revision = this.manager.AggregateRevision;
                this.RefreshSnapshotLocked();
                return new AgentInputQueueCommandResult
                {
                    CommandId = request.CommandId,
                    Status = AgentInputQueueCommandStatus.Applied,
                    QueueId = queue.QueueId,
                    Revision = revision,
                };
            }
        }));
    }

    /// <summary>Marks an owner-consumed item id so future edit attempts return <c>Rejected</c>.</summary>
    internal void MarkItemConsumed(string itemId)
    {
        if (string.IsNullOrEmpty(itemId))
        {
            return;
        }
        lock (this.stateLock)
        {
            this.consumedItemIds.Add(itemId);
        }
    }

    private AgentInputQueueCommandResult Execute<TRequest>(
        TRequest request,
        Guid commandId,
        long expectedRevision,
        Func<AgentInputQueueCommandResult> apply)
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(LocalAgentInputQueuesAdapter));
        }
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        }
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("Command id must not be empty.", nameof(commandId));
        }

        var payload = JsonSerializer.Serialize(request, AIJsonUtilities.DefaultOptions);

        AgentInputQueueCommandResult result;
        lock (this.stateLock)
        {
            if (this.commandLog.TryGetValue(commandId, out var prior))
            {
                return string.Equals(prior.Payload, payload, StringComparison.Ordinal)
                    ? prior.Result with { Status = AgentInputQueueCommandStatus.Duplicate }
                    : new AgentInputQueueCommandResult
                    {
                        CommandId = commandId,
                        Status = AgentInputQueueCommandStatus.Conflict,
                        Revision = this.manager.AggregateRevision,
                        CurrentSnapshot = this.snapshot,
                    };
            }

            var currentRevision = this.manager.AggregateRevision;
            if (currentRevision != expectedRevision)
            {
                var conflict = new AgentInputQueueCommandResult
                {
                    CommandId = commandId,
                    Status = AgentInputQueueCommandStatus.Conflict,
                    Revision = currentRevision,
                    CurrentSnapshot = this.snapshot,
                };
                this.commandLog[commandId] = (payload, conflict);
                return conflict;
            }

            try
            {
                this.commandInProgress = true;
                result = apply();
            }
            finally
            {
                this.commandInProgress = false;
            }
            this.commandLog[commandId] = (payload, result);
        }
        if (result.Status == AgentInputQueueCommandStatus.Applied)
        {
            this.RaiseChanged();
        }
        return result;
    }

    private static ChatMessage[] CopyMessagesForOwner(IReadOnlyList<ChatMessage> messages)
    {
        // #1485: deep-copy on ingress so retained caller references cannot mutate owner state.
        var json = JsonSerializer.Serialize(messages, AIJsonUtilities.DefaultOptions);
        return JsonSerializer.Deserialize<ChatMessage[]>(json, AIJsonUtilities.DefaultOptions)
            ?? Array.Empty<ChatMessage>();
    }

    private AgentInputQueueCommandResult Reject(Guid commandId, string errorCode, string? queueId, string? itemId) => new()
    {
        CommandId = commandId,
        Status = AgentInputQueueCommandStatus.Rejected,
        QueueId = queueId,
        ItemId = itemId,
        Revision = this.manager.AggregateRevision,
        ErrorCode = errorCode,
    };

    private static (bool ok, string? err) ValidateConfiguration(AgentInputQueueConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.Name))
        {
            return (false, AgentInputQueueErrorCodes.InvalidRequest);
        }
        if (configuration.Priority < 0)
        {
            return (false, AgentInputQueueErrorCodes.InvalidRequest);
        }
        if (!Enum.IsDefined(configuration.Immediacy))
        {
            return (false, AgentInputQueueErrorCodes.InvalidRequest);
        }
        return (true, null);
    }

    private void RefreshSnapshotLocked()
    {
        this.snapshot = this.BuildSnapshotLocked();
        foreach (var q in this.queues)
        {
            q.RaiseChanged();
        }
    }

    private AgentInputQueuesSnapshot BuildSnapshot()
    {
        lock (this.stateLock)
        {
            return this.BuildSnapshotLocked();
        }
    }

    private AgentInputQueuesSnapshot BuildSnapshotLocked()
    {
        var snaps = ImmutableArray.CreateBuilder<AgentInputQueueSnapshot>(this.queues.Count);
        foreach (var q in this.queues)
        {
            snaps.Add(q.CaptureSnapshot());
        }
        return new AgentInputQueuesSnapshot
        {
            Revision = this.manager.AggregateRevision,
            Queues = snaps.ToImmutable(),
        };
    }

    private void RaiseChanged()
    {
        var handler = this.Changed;
        if (handler is null)
        {
            return;
        }
        var ctx = this.foregroundContext;
        if (ctx is null || SynchronizationContext.Current == ctx)
        {
            handler.Invoke(this, EventArgs.Empty);
            return;
        }
        ctx.Post(_ => this.Changed?.Invoke(this, EventArgs.Empty), null);
    }

    private void OnManagerQueueStateChanged(object? sender, AgentInputQueueManager.QueueStateChangedEventArgs e)
    {
        if (this.commandInProgress || this.disposed)
        {
            return;
        }

        // The change may have originated outside our command surface (e.g. the owner's Copilot
        // dequeue path). Re-snapshot so external consumers observe a fresh aggregate.
        lock (this.stateLock)
        {
            this.RefreshSnapshotLocked();
        }
        this.RaiseChanged();
    }

    private void OnManagerQueuePublished(object? sender, AgentInputQueueManager.QueuePublishedEventArgs e)
        => this.MarkItemConsumed(e.Item.ItemId);

    private sealed class LocalAgentInputQueue : IAgentInputQueue
    {
        private readonly LocalAgentInputQueuesAdapter parent;
        private string name;
        private AgentInputQueueSnapshot snapshot;
        public event EventHandler? Changed;

        internal LocalAgentInputQueue(
            LocalAgentInputQueuesAdapter parent,
            AgentInputQueue underlying,
            string name,
            bool isDefault,
            bool isImmediate)
        {
            this.parent = parent;
            this.Underlying = underlying;
            this.name = name;
            this.IsDefault = isDefault;
            this.IsImmediate = isImmediate;
            this.snapshot = this.CaptureSnapshotCore();
        }

        public AgentInputQueue Underlying { get; }
        public string QueueId => this.Underlying.QueueId;
        public string Name => this.name;
        public bool IsDefault { get; }
        public bool IsImmediate { get; }

        public AgentInputQueueSnapshot Snapshot
        {
            get
            {
                lock (this.parent.stateLock)
                {
                    return this.snapshot;
                }
            }
        }

        internal void Rename(string newName) => this.name = newName;

        internal void RaiseChanged() => this.Changed?.Invoke(this, EventArgs.Empty);

        internal AgentInputQueueSnapshot CaptureSnapshot()
        {
            this.snapshot = this.CaptureSnapshotCore();
            return this.snapshot;
        }

        private AgentInputQueueSnapshot CaptureSnapshotCore()
        {
            var items = this.Underlying.Items;
            var builder = ImmutableArray.CreateBuilder<AgentInputItemSnapshot>(items.Count);
            foreach (var item in items)
            {
                builder.Add(new AgentInputItemSnapshot
                {
                    ItemId = item.ItemId,
                    Messages = CloneMessages(item.Messages),
                });
            }

            return new AgentInputQueueSnapshot
            {
                QueueId = this.QueueId,
                Name = this.name,
                IsDefault = this.IsDefault,
                IsImmediate = this.IsImmediate,
                Immediacy = this.Underlying.Immediacy,
                Priority = this.Underlying.Priority,
                CoalescingKey = this.Underlying.CoalescingKey,
                Revision = this.Underlying.Revision,
                Items = builder.ToImmutable(),
            };
        }

        private static ImmutableArray<ChatMessage> CloneMessages(ChatMessage[]? messages)
        {
            if (messages is null || messages.Length == 0)
            {
                return ImmutableArray<ChatMessage>.Empty;
            }

            var json = JsonSerializer.Serialize(messages, AIJsonUtilities.DefaultOptions);
            return JsonSerializer.Deserialize<ChatMessage[]>(json, AIJsonUtilities.DefaultOptions)!
                .ToImmutableArray();
        }
    }
}
