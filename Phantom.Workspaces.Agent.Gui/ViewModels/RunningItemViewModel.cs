using System.Collections.ObjectModel;
using System.Linq;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class RunningItemViewModel : ViewModelBase, IDisposable
{
    public RunningItemViewModel(AgentChatRunningItem source)
    {
        this.Source = source;
        this.HistoryItems = [];
        this.SyncHistoryItems(this.Source.Items.ToArray());
    }

    internal AgentChatRunningItem Source { get; }

    public ObservableCollection<ChatHistoryItemViewModel> HistoryItems { get; }

    internal void UpdateModel()
    {
        this.SyncHistoryItems(this.Source.Items.ToArray());
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

    private void SyncHistoryItems(AgentChatHistoryItem[] items)
    {
        for (var i = 0; i < items.Length; i++)
        {
            if (i < this.HistoryItems.Count)
            {
                this.HistoryItems[i].UpdateFrom(items[i]);
            }
            else
            {
                this.HistoryItems.Add(new ChatHistoryItemViewModel(items[i], isInProgress: true));
            }
        }

        while (this.HistoryItems.Count > items.Length)
        {
            var lastIndex = this.HistoryItems.Count - 1;
            this.HistoryItems[lastIndex].Dispose();
            this.HistoryItems.RemoveAt(lastIndex);
        }
    }
}
