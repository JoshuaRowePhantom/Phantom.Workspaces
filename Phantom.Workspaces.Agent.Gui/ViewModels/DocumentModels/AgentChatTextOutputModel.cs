using System.Collections.Specialized;
using System.Linq;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

internal sealed class AgentChatTextOutputModel : IDisposable
{
    private readonly IReadOnlyList<AgentChatHistoryItem> historyItems;
    private readonly IReadOnlyList<AgentChatRunningItem> runningItems;
    private readonly Func<bool> isReasoningVisible;
    private readonly Action<string> setOutputText;
    private readonly Dictionary<AgentChatRunningItem, NotifyCollectionChangedEventHandler> runningItemHandlers = [];

    public AgentChatTextOutputModel(
        IReadOnlyList<AgentChatHistoryItem> historyItems,
        IReadOnlyList<AgentChatRunningItem> runningItems,
        Func<bool> isReasoningVisible,
        Action<string> setOutputText)
    {
        this.historyItems = historyItems;
        this.runningItems = runningItems;
        this.isReasoningVisible = isReasoningVisible;
        this.setOutputText = setOutputText;

        if (historyItems is INotifyCollectionChanged historyChanged)
        {
            historyChanged.CollectionChanged += this.OnHistoryCollectionChanged;
        }

        if (runningItems is INotifyCollectionChanged runningChanged)
        {
            runningChanged.CollectionChanged += this.OnRunningCollectionChanged;
        }

        this.SyncRunningItemSubscriptions();
        this.Refresh();
    }

    public void Dispose()
    {
        if (this.historyItems is INotifyCollectionChanged historyChanged)
        {
            historyChanged.CollectionChanged -= this.OnHistoryCollectionChanged;
        }

        if (this.runningItems is INotifyCollectionChanged runningChanged)
        {
            runningChanged.CollectionChanged -= this.OnRunningCollectionChanged;
        }

        foreach (var pair in this.runningItemHandlers)
        {
            pair.Key.Items.CollectionChanged -= pair.Value;
        }

        this.runningItemHandlers.Clear();
    }

    public void Refresh()
    {
        this.setOutputText(
            ChatOutputTextFormatter.BuildTranscript(
                this.historyItems,
                this.runningItems,
                this.isReasoningVisible()));
    }

    private void OnHistoryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.Refresh();
    }

    private void OnRunningCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.SyncRunningItemSubscriptions();
        this.Refresh();
    }

    private void OnRunningItemMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.Refresh();
    }

    private void SyncRunningItemSubscriptions()
    {
        var removedItems = this.runningItemHandlers.Keys.Except(this.runningItems).ToArray();
        foreach (var removedItem in removedItems)
        {
            var handler = this.runningItemHandlers[removedItem];
            removedItem.Items.CollectionChanged -= handler;
            this.runningItemHandlers.Remove(removedItem);
        }

        foreach (var runningItem in this.runningItems)
        {
            if (this.runningItemHandlers.ContainsKey(runningItem))
            {
                continue;
            }

            NotifyCollectionChangedEventHandler handler = this.OnRunningItemMessagesChanged;
            runningItem.Items.CollectionChanged += handler;
            this.runningItemHandlers[runningItem] = handler;
        }
    }
}
