using System.Collections.ObjectModel;

namespace Phantom.Workspaces.Llm;

public sealed class AgentChatRunningItemCollection : ReadOnlyObservableCollection<AgentChatRunningItem>
{
    private readonly ObservableCollection<AgentChatRunningItem> items;

    public AgentChatRunningItemCollection()
        : this(new ObservableCollection<AgentChatRunningItem>())
    {
    }

    private AgentChatRunningItemCollection(ObservableCollection<AgentChatRunningItem> items)
        : base(items)
    {
        this.items = items;
    }

    internal void Add(AgentChatRunningItem item) => this.items.Add(item);

    internal void Insert(int index, AgentChatRunningItem item) => this.items.Insert(index, item);

    internal void SetItem(int index, AgentChatRunningItem item) => this.items[index] = item;

    internal void RemoveAt(int index) => this.items.RemoveAt(index);

    internal bool Remove(AgentChatRunningItem item) => this.items.Remove(item);

    internal void Clear() => this.items.Clear();
}
