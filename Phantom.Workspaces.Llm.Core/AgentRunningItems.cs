using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Collections.ObjectModel;

namespace Phantom.Workspaces.Llm;

public sealed class AgentRunningItems
{
    // Collection notifications run synchronously while this reentrant monitor is held. Snapshot
    // consumers can therefore copy both collection levels without blocking the foreground scheduler.
    private readonly object gate = new();
    private readonly AgentChatRunningItemCollection items;

    public AgentRunningItems(AgentChatRunningItemCollection items)
    {
        ArgumentNullException.ThrowIfNull(items);
        this.items = items;
    }

    public AgentChatRunningItem Create(params AgentChatHistoryItem[] items)
    {
        lock (this.gate)
        {
            var runningItem = new AgentChatRunningItem();
            SyncItems(runningItem.Items, items);
            this.items.Add(runningItem);
            return runningItem;
        }
    }

    public void Update(AgentChatRunningItem runningItem, AgentChatHistoryItem[] items)
    {
        ArgumentNullException.ThrowIfNull(runningItem);
        ArgumentNullException.ThrowIfNull(items);
        lock (this.gate)
        {
            // SyncItems raises fine-grained Add/Remove/Replace notifications on runningItem.Items.
            // The outer AgentChatRunningItemCollection no longer needs a synthetic Replace
            // notification, since the running item's identity has not changed.
            SyncItems(runningItem.Items, items);
        }
    }

    public void Remove(AgentChatRunningItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (this.gate)
            this.items.Remove(item);
    }

    internal ImmutableArray<AgentChatHistoryItem> CaptureItems(
        AgentChatRunningItem runningItem)
    {
        ArgumentNullException.ThrowIfNull(runningItem);
        lock (this.gate)
            return runningItem.Items.ToImmutableArray();
    }

    internal void SubscribeAndCapture(
        NotifyCollectionChangedEventHandler runningItemsChanged,
        NotifyCollectionChangedEventHandler runningItemChanged,
        Action<ImmutableArray<AgentChatRunningItemSnapshot>> initialize)
    {
        ArgumentNullException.ThrowIfNull(runningItemsChanged);
        ArgumentNullException.ThrowIfNull(runningItemChanged);
        ArgumentNullException.ThrowIfNull(initialize);

        lock (this.gate)
        {
            var subscribedItems = new List<AgentChatRunningItem>();
            ((INotifyCollectionChanged)this.items).CollectionChanged += runningItemsChanged;
            try
            {
                var snapshots = ImmutableArray.CreateBuilder<AgentChatRunningItemSnapshot>(
                    this.items.Count);
                foreach (var item in this.items)
                {
                    item.Items.CollectionChanged += runningItemChanged;
                    subscribedItems.Add(item);
                    snapshots.Add(new AgentChatRunningItemSnapshot(
                        item,
                        item.Items.ToImmutableArray()));
                }
                initialize(snapshots.MoveToImmutable());
            }
            catch
            {
                foreach (var item in subscribedItems)
                    item.Items.CollectionChanged -= runningItemChanged;
                foreach (var item in this.items)
                    item.Items.CollectionChanged -= runningItemChanged;
                ((INotifyCollectionChanged)this.items).CollectionChanged -= runningItemsChanged;
                throw;
            }
        }
    }

    internal void Unsubscribe(
        NotifyCollectionChangedEventHandler runningItemsChanged,
        NotifyCollectionChangedEventHandler runningItemChanged,
        IReadOnlyList<AgentChatRunningItem> subscribedItems)
    {
        ArgumentNullException.ThrowIfNull(runningItemsChanged);
        ArgumentNullException.ThrowIfNull(runningItemChanged);
        ArgumentNullException.ThrowIfNull(subscribedItems);

        lock (this.gate)
        {
            ((INotifyCollectionChanged)this.items).CollectionChanged -= runningItemsChanged;
            foreach (var item in subscribedItems)
                item.Items.CollectionChanged -= runningItemChanged;
        }
    }

    private static bool SyncItems(ObservableCollection<AgentChatHistoryItem> target, IReadOnlyList<AgentChatHistoryItem> source)
    {
        var changed = false;
        for (var index = 0; index < source.Count; index++)
        {
            if (index < target.Count)
            {
                if (!ReferenceEquals(target[index], source[index]))
                {
                    target[index] = source[index];
                    changed = true;
                }
            }
            else
            {
                target.Add(source[index]);
                changed = true;
            }
        }

        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
            changed = true;
        }

        return changed;
    }
}
