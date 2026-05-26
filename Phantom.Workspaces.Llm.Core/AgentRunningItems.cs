using System.Collections.ObjectModel;

namespace Phantom.Workspaces.Llm;

public sealed class AgentRunningItems
{
    private readonly ObservableCollection<AgentChatRunningItem> items;

    public event EventHandler? Idle;

    public AgentRunningItems(ObservableCollection<AgentChatRunningItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        this.items = items;
    }

    public AgentChatRunningItem Create(params AgentChatHistoryItem[] items)
    {
        var runningItem = new AgentChatRunningItem();
        runningItem.Items = items;
        this.items.Add(runningItem);
        return runningItem;
    }

    public void Update(AgentChatRunningItem runningItem, AgentChatHistoryItem[] items)
    {
        ArgumentNullException.ThrowIfNull(runningItem);
        ArgumentNullException.ThrowIfNull(items);
        runningItem.Items = items;
        var index = this.items.IndexOf(runningItem);
        if (index >= 0)
        {
            this.items[index] = runningItem;
        }
    }

    public void Remove(AgentChatRunningItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var removed = this.items.Remove(item);
        if (removed && this.items.Count == 0)
        {
            this.Idle?.Invoke(this, EventArgs.Empty);
        }
    }
}
