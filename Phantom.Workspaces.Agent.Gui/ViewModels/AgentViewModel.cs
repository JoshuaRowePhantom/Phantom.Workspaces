using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly AgentChat agentChat;
    private bool isReasoningVisible;

    public AgentViewModel(AgentChat agentChat, string displayName)
    {
        this.agentChat = agentChat;
        this.DisplayName = displayName;
        this.InputQueue = new InputQueueViewModel(
            this.agentChat,
            this.agentChat.DefaultInputQueue,
            this.agentChat.InputQueueManager);

        agentChat.History.CollectionChanged += this.OnHistoryChanged;
        agentChat.RunningItems.CollectionChanged += this.OnRunningItemsChanged;
    }

    public string DisplayName { get; }

    public AgentChat AgentChat => this.agentChat;

    public InputQueueViewModel InputQueue { get; }

    public ObservableCollection<ChatHistoryItemViewModel> History { get; } = [];

    public ObservableCollection<RunningItemViewModel> RunningItems { get; } = [];

    public bool IsReasoningVisible
    {
        get => this.isReasoningVisible;
        private set => this.SetProperty(ref this.isReasoningVisible, value);
    }

    public void ToggleReasoningVisibility() => this.SetReasoningVisibility(!this.IsReasoningVisible);

    public void SetReasoningVisibility(bool visible)
    {
        if (!this.SetProperty(ref this.isReasoningVisible, visible))
        {
            return;
        }

        foreach (var item in this.History)
        {
            item.SetReasoningVisible(visible);
        }
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
            {
                foreach (AgentChatHistoryItem item in e.NewItems)
                {
                    var vm = new ChatHistoryItemViewModel(item);
                    vm.SetReasoningVisible(this.IsReasoningVisible);
                    this.History.Add(vm);
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Replace && e.NewItems is not null && e.NewStartingIndex >= 0)
            {
                var index = e.NewStartingIndex;
                foreach (AgentChatHistoryItem item in e.NewItems)
                {
                    if (index >= 0 && index < this.History.Count)
                    {
                        this.History[index].UpdateFrom(item);
                    }

                    index++;
                }
            }
        });
    }

    private void OnRunningItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                    foreach (AgentChatRunningItem item in e.NewItems)
                    {
                        this.RunningItems.Add(new RunningItemViewModel(item));
                    }
                    break;

                case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                    foreach (AgentChatRunningItem item in e.OldItems)
                    {
                        var vm = this.RunningItems.FirstOrDefault(x => x.Source == item);
                        if (vm is not null)
                        {
                            this.RunningItems.Remove(vm);
                        }
                    }
                    break;

                case NotifyCollectionChangedAction.Replace when e.NewItems is not null:
                    foreach (AgentChatRunningItem item in e.NewItems)
                    {
                        var vm = this.RunningItems.FirstOrDefault(x => x.Source == item);
                        vm?.UpdateText();
                    }
                    break;
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var item in this.History)
        {
            item.Dispose();
        }

        this.InputQueue.Dispose();
        this.agentChat.History.CollectionChanged -= this.OnHistoryChanged;
        this.agentChat.RunningItems.CollectionChanged -= this.OnRunningItemsChanged;
        await this.agentChat.DisposeAsync();
    }
}
