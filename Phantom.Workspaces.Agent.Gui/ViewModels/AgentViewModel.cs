using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia.Threading;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly AgentChat agentChat;
    private bool isReasoningVisible;
    private string agentSessionId;

    public AgentViewModel(AgentChat agentChat, string displayName)
    {
        this.agentChat = agentChat;
        this.agentSessionId = agentChat.AgentSessionId;
        this.DisplayName = displayName;
        this.InterruptCommand = new RelayCommand(agentChat.Interrupt);
        this.InputQueue = new InputQueueViewModel(
            this.agentChat,
            this.agentChat.DefaultInputQueue,
            this.agentChat.InputQueueManager);

        this.LoadCurrentHistory();
        this.LoadCurrentRunningItems();

        agentChat.History.CollectionChanged += this.OnHistoryChanged;
        agentChat.RunningItems.CollectionChanged += this.OnRunningItemsChanged;
        agentChat.AgentSessionIdChanged += this.OnAgentSessionIdChanged;
    }

    public string DisplayName { get; }

    public string AgentSessionId
    {
        get => this.agentSessionId;
        private set => this.SetProperty(ref this.agentSessionId, value);
    }

    public AgentChat AgentChat => this.agentChat;

    public ICommand InterruptCommand { get; }

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

        foreach (var item in this.RunningItems)
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
                        var vm = new RunningItemViewModel(item);
                        vm.SetReasoningVisible(this.IsReasoningVisible);
                        this.RunningItems.Add(vm);
                    }
                    break;

                case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                    foreach (AgentChatRunningItem item in e.OldItems)
                    {
                        var vm = this.RunningItems.FirstOrDefault(x => x.Source == item);
                        if (vm is not null)
                        {
                            this.RunningItems.Remove(vm);
                            vm.Dispose();
                        }
                    }
                    break;

                case NotifyCollectionChangedAction.Replace when e.NewItems is not null:
                    foreach (AgentChatRunningItem item in e.NewItems)
                    {
                        var vm = this.RunningItems.FirstOrDefault(x => x.Source == item);
                        vm?.UpdateModel();
                    }
                    break;
            }
        });
    }

    private void OnAgentSessionIdChanged(object? sender, string nextAgentSessionId)
    {
        Dispatcher.UIThread.Post(() => this.AgentSessionId = nextAgentSessionId);
    }

    private void LoadCurrentHistory()
    {
        foreach (var item in this.agentChat.History)
        {
            var vm = new ChatHistoryItemViewModel(item);
            vm.SetReasoningVisible(this.IsReasoningVisible);
            this.History.Add(vm);
        }
    }

    private void LoadCurrentRunningItems()
    {
        foreach (var item in this.agentChat.RunningItems)
        {
            var vm = new RunningItemViewModel(item);
            vm.SetReasoningVisible(this.IsReasoningVisible);
            this.RunningItems.Add(vm);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var item in this.History)
        {
            item.Dispose();
        }

        foreach (var item in this.RunningItems)
        {
            item.Dispose();
        }

        this.InputQueue.Dispose();
        this.agentChat.History.CollectionChanged -= this.OnHistoryChanged;
        this.agentChat.RunningItems.CollectionChanged -= this.OnRunningItemsChanged;
        this.agentChat.AgentSessionIdChanged -= this.OnAgentSessionIdChanged;
        await this.agentChat.DisposeAsync();
    }
}
