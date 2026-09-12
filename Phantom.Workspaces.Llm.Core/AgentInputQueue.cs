using System.Collections.Immutable;
using System.Threading;

namespace Phantom.Workspaces.Llm;

public enum AgentInputQueueImmediacy
{
    Immediate,
    Queue,
    Held,
}

public sealed class AgentInputQueue
{
    public event EventHandler? Changed;
    public event EventHandler? ConfigurationChanged;

    public sealed record Parameters
    {
        public int Priority { get; init; }

        public AgentInputQueueImmediacy Immediacy { get; init; } = AgentInputQueueImmediacy.Queue;

        public string? CoalescingKey { get; init; }

        /// <summary>
        /// Optional stable queue identifier (issue #1485). When null or blank, a fresh
        /// <c>Guid.NewGuid().ToString("n")</c> is assigned so every queue has a nonblank id.
        /// </summary>
        public string? QueueId { get; init; }

        /// <summary>Optional display name (issue #1485).</summary>
        public string? Name { get; init; }
    }

    private ImmutableList<AgentInputItem> items;
    private long revision;

    public AgentInputQueue(
        Parameters? parameters = null)
    {
        parameters ??= new Parameters();

        this.QueueId = string.IsNullOrWhiteSpace(parameters.QueueId)
            ? Guid.NewGuid().ToString("n")
            : parameters.QueueId!;
        this.Priority = parameters.Priority;
        this.Immediacy = parameters.Immediacy;
        this.CoalescingKey = parameters.CoalescingKey;
        this.items = ImmutableList<AgentInputItem>.Empty;
    }

    /// <summary>
    /// Stable nonblank owner-authoritative queue identifier assigned at construction (issue #1485).
    /// Common-surface commands identify a queue by this id.
    /// </summary>
    public string QueueId { get; }

    /// <summary>
    /// Monotonically increasing per-queue revision. Bumped once for every applied mutation
    /// (enqueue/edit/remove/configure/move-in/move-out). Owner-authoritative.
    /// </summary>
    public long Revision => Volatile.Read(ref this.revision);

    public int Priority { get; private set; }

    public AgentInputQueueImmediacy Immediacy { get; private set; }

    public string? CoalescingKey { get; private set; }

    public ImmutableList<AgentInputItem> Items => Volatile.Read(ref this.items);

    public void Configure(
        Parameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        this.Priority = parameters.Priority;
        this.Immediacy = parameters.Immediacy;
        this.CoalescingKey = parameters.CoalescingKey;
        Interlocked.Increment(ref this.revision);
        this.OnChanged();
        this.OnConfigurationChanged();
    }

    public ImmutableList<AgentInputItem> Enqueue(
        IEnumerable<AgentInputItem> newItems)
    {
        ArgumentNullException.ThrowIfNull(newItems);

        var appendedItems = newItems.ToImmutableList();
        if (appendedItems.Count == 0)
        {
            return this.Items;
        }

        while (true)
        {
            var existingItems = this.Items;
            var updatedItems = existingItems.AddRange(appendedItems);
            var expectedItems = existingItems;
            if (this.Update(ref expectedItems, updatedItems))
            {
                return updatedItems;
            }
        }
    }

    public bool Update(
        ref ImmutableList<AgentInputItem> existingItems,
        ImmutableList<AgentInputItem> newItems)
    {
        ArgumentNullException.ThrowIfNull(existingItems);
        ArgumentNullException.ThrowIfNull(newItems);

        var observedItems = Interlocked.CompareExchange(
                   ref this.items,
                   newItems,
                   existingItems);

        var succeeded = observedItems == existingItems;
        existingItems = observedItems;
        if (succeeded)
        {
            Interlocked.Increment(ref this.revision);
            this.OnChanged();
        }
        return succeeded;
    }

    public bool Clear(
        ref ImmutableList<AgentInputItem> existingItems)
    {
        ArgumentNullException.ThrowIfNull(existingItems);
        return this.Update(ref existingItems, ImmutableList<AgentInputItem>.Empty);
    }

    public bool TryRemoveAt(
        ref ImmutableList<AgentInputItem> existingItems,
        int index)
    {
        ArgumentNullException.ThrowIfNull(existingItems);
        if (index < 0 || index >= existingItems.Count)
        {
            return false;
        }

        var newItems = existingItems.RemoveAt(index);
        return this.Update(ref existingItems, newItems);
    }

    public bool TryRemove(
        ref ImmutableList<AgentInputItem> existingItems,
        AgentInputItem item)
    {
        ArgumentNullException.ThrowIfNull(existingItems);
        ArgumentNullException.ThrowIfNull(item);

        var index = FindIndexByReference(existingItems, item);
        if (index < 0)
        {
            return false;
        }

        return this.TryRemoveAt(ref existingItems, index);
    }

    public bool TryUpdateAt(
        ref ImmutableList<AgentInputItem> existingItems,
        int index,
        AgentInputItem newItem)
    {
        ArgumentNullException.ThrowIfNull(existingItems);
        ArgumentNullException.ThrowIfNull(newItem);
        if (index < 0 || index >= existingItems.Count)
        {
            return false;
        }

        var newItems = existingItems.SetItem(index, newItem);
        return this.Update(ref existingItems, newItems);
    }

    public bool TryUpdate(
        ref ImmutableList<AgentInputItem> existingItems,
        AgentInputItem item,
        AgentInputItem newItem)
    {
        ArgumentNullException.ThrowIfNull(existingItems);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(newItem);

        var index = FindIndexByReference(existingItems, item);
        if (index < 0)
        {
            return false;
        }

        return this.TryUpdateAt(ref existingItems, index, newItem);
    }

    private static int FindIndexByReference(
        ImmutableList<AgentInputItem> items,
        AgentInputItem item)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], item))
            {
                return index;
            }
        }

        return -1;
    }

    private void OnChanged()
    {
        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnConfigurationChanged()
    {
        this.ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }
}
