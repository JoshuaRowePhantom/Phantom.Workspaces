using System.Collections.Immutable;
using System.Threading;
using Microsoft.Extensions.AI;

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

    public sealed record Parameters
    {
        public int Priority { get; init; }

        public AgentInputQueueImmediacy Immediacy { get; init; } = AgentInputQueueImmediacy.Queue;

        public string? CoalescingKey { get; init; }
    }

    private ImmutableList<ChatMessage> items;

    public AgentInputQueue(
        Parameters? parameters = null)
    {
        parameters ??= new Parameters();

        this.Priority = parameters.Priority;
        this.Immediacy = parameters.Immediacy;
        this.CoalescingKey = parameters.CoalescingKey;
        this.items = ImmutableList<ChatMessage>.Empty;
    }

    public int Priority { get; private set; }

    public AgentInputQueueImmediacy Immediacy { get; private set; }

    public string? CoalescingKey { get; private set; }

    public ImmutableList<ChatMessage> Items => Volatile.Read(ref this.items);

    public void Configure(
        Parameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        this.Priority = parameters.Priority;
        this.Immediacy = parameters.Immediacy;
        this.CoalescingKey = parameters.CoalescingKey;
        this.OnChanged();
    }

    public ImmutableList<ChatMessage> Enqueue(
        IEnumerable<ChatMessage> newItems)
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
        ref ImmutableList<ChatMessage> existingItems,
        ImmutableList<ChatMessage> newItems)
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
            this.OnChanged();
        }
        return succeeded;
    }

    public bool Clear(
        ref ImmutableList<ChatMessage> existingItems)
    {
        ArgumentNullException.ThrowIfNull(existingItems);
        return this.Update(ref existingItems, ImmutableList<ChatMessage>.Empty);
    }

    public bool TryRemoveAt(
        ref ImmutableList<ChatMessage> existingItems,
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

    public bool TryUpdateAt(
        ref ImmutableList<ChatMessage> existingItems,
        int index,
        ChatMessage newItem)
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

    private void OnChanged()
    {
        this.Changed?.Invoke(this, EventArgs.Empty);
    }
}
