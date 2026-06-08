using System.Collections.ObjectModel;

namespace Phantom.Workspaces.Llm;

public sealed class AgentChatHistoryCollection : ReadOnlyObservableCollection<AgentChatHistoryItem>
{
    private readonly ObservableCollection<AgentChatHistoryItem> items;

    public AgentChatHistoryCollection()
        : this(new ObservableCollection<AgentChatHistoryItem>())
    {
    }

    private AgentChatHistoryCollection(ObservableCollection<AgentChatHistoryItem> items)
        : base(items)
    {
        this.items = items;
    }

    internal void Add(AgentChatHistoryItem item) => this.items.Add(item);

    internal void Insert(int index, AgentChatHistoryItem item) => this.items.Insert(index, item);

    internal void RemoveAt(int index) => this.items.RemoveAt(index);

    internal bool Remove(AgentChatHistoryItem item) => this.items.Remove(item);

    internal void Clear() => this.items.Clear();
}
