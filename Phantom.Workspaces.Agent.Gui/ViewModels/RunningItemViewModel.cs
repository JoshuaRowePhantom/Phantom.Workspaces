using System.Collections.ObjectModel;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class RunningItemViewModel : ViewModelBase, IDisposable
{
    public RunningItemViewModel(AgentChatRunningItem source)
    {
        this.Source = source;
        this.HistoryItems = [];
        this.SyncHistoryItems(this.Source.Items);
    }

    internal AgentChatRunningItem Source { get; }

    public ObservableCollection<ChatHistoryItemViewModel> HistoryItems { get; }

    internal void UpdateModel()
    {
        this.SyncHistoryItems(this.Source.Items);
    }

    internal void SetReasoningVisible(bool visible)
    {
        foreach (var historyItem in this.HistoryItems)
        {
            historyItem.SetReasoningVisible(visible);
        }
    }

    public void Dispose()
    {
        foreach (var historyItem in this.HistoryItems)
        {
            historyItem.Dispose();
        }
    }

    private void SyncHistoryItems(AgentChatHistoryItem[]? items)
    {
        var runningItems = items ?? [];

        for (var i = 0; i < runningItems.Length; i++)
        {
            if (i < this.HistoryItems.Count)
            {
                this.HistoryItems[i].UpdateFrom(runningItems[i]);
            }
            else
            {
                this.HistoryItems.Add(new ChatHistoryItemViewModel(runningItems[i]));
            }
        }

        while (this.HistoryItems.Count > runningItems.Length)
        {
            var lastIndex = this.HistoryItems.Count - 1;
            this.HistoryItems[lastIndex].Dispose();
            this.HistoryItems.RemoveAt(lastIndex);
        }
    }
}
