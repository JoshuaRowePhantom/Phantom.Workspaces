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
    public sealed record Parameters
    {
        public int Priority { get; init; }

        public AgentInputQueueImmediacy Immediacy { get; init; } = AgentInputQueueImmediacy.Queue;

        public string? CoalescingKey { get; init; }
    }

    private ImmutableList<LlmEvent> items;

    public AgentInputQueue(
        Parameters? parameters = null)
    {
        parameters ??= new Parameters();

        this.Priority = parameters.Priority;
        this.Immediacy = parameters.Immediacy;
        this.CoalescingKey = parameters.CoalescingKey;
        this.items = ImmutableList<LlmEvent>.Empty;
    }

    public int Priority { get; private set; }

    public AgentInputQueueImmediacy Immediacy { get; private set; }

    public string? CoalescingKey { get; private set; }

    public ImmutableList<LlmEvent> Items => Volatile.Read(ref this.items);

    public void Configure(
        Parameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        this.Priority = parameters.Priority;
        this.Immediacy = parameters.Immediacy;
        this.CoalescingKey = parameters.CoalescingKey;
    }

    public ImmutableList<LlmEvent> Enqueue(
        IEnumerable<LlmEvent> newItems)
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
            if (this.Update(existingItems, updatedItems))
            {
                return updatedItems;
            }
        }
    }

    public bool Update(
        ImmutableList<LlmEvent> existingItems,
        ImmutableList<LlmEvent> newItems)
    {
        ArgumentNullException.ThrowIfNull(existingItems);
        ArgumentNullException.ThrowIfNull(newItems);

        return Interlocked.CompareExchange(
                   ref this.items,
                   newItems,
                   existingItems) == existingItems;
    }
}
