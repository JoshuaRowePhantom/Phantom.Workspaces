using System.Collections.ObjectModel;

namespace Phantom.Workspaces.Llm;

public sealed class AgentRunningItems
{
    private readonly AgentChatRunningItemCollection items;

    public AgentRunningItems(AgentChatRunningItemCollection items)
    {
        ArgumentNullException.ThrowIfNull(items);
        this.items = items;
    }

    public AgentChatRunningItem Create(params AgentChatHistoryItem[] items)
    {
        var runningItem = new AgentChatRunningItem();
        SyncItems(runningItem.Items, items);
        this.items.Add(runningItem);
        return runningItem;
    }

    public void Update(AgentChatRunningItem runningItem, AgentChatHistoryItem[] items)
    {
        ArgumentNullException.ThrowIfNull(runningItem);
        ArgumentNullException.ThrowIfNull(items);
        // SyncItems raises fine-grained Add/Remove/Replace notifications on runningItem.Items.
        // The outer AgentChatRunningItemCollection no longer needs a synthetic Replace
        // notification, since the running item's identity has not changed.
        SyncItems(runningItem.Items, items);
    }

    public void Remove(AgentChatRunningItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        this.items.Remove(item);
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
