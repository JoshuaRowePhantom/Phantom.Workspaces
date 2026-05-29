using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class AgentViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly AgentChat agentChat;
    private readonly object stateLock = new();
    private long appliedStateVersion;
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

        agentChat.StateChanged += this.OnStateChanged;
        this.ApplySnapshot(agentChat.GetStateSnapshot());
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

    private void OnStateChanged(object? sender, AgentChatStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            lock (this.stateLock)
            {
                if (e.FromVersion != this.appliedStateVersion)
                {
                    this.ApplySnapshot(this.agentChat.GetStateSnapshot());
                    return;
                }
                this.ApplyIncrementalChange(e);
                this.appliedStateVersion = e.ToVersion;
            }
        });
    }

    private void ApplySnapshot(AgentChatStateSnapshot snapshot)
    {
        lock (this.stateLock)
        {
            foreach (var historyItem in this.History)
            {
                historyItem.Dispose();
            }

            foreach (var runningItem in this.RunningItems)
            {
                runningItem.Dispose();
            }

            this.History.Clear();
            this.RunningItems.Clear();

            foreach (var item in snapshot.History)
            {
                this.History.Add(this.CreateHistoryViewModel(item));
            }

            foreach (var item in snapshot.RunningItems)
            {
                this.RunningItems.Add(this.CreateRunningItemViewModel(item));
            }

            this.AgentSessionId = snapshot.AgentSessionId;
            this.appliedStateVersion = snapshot.Version;
        }
    }

    private void ApplyIncrementalChange(AgentChatStateChangedEventArgs change)
    {
        switch (change.ChangeKind)
        {
            case AgentChatStateChangeKind.HistoryAdded when change.HistoryItem is not null:
                this.History.Add(this.CreateHistoryViewModel(change.HistoryItem));
                break;
            case AgentChatStateChangeKind.HistoryReplaced when change.HistoryItem is not null && change.Index >= 0 && change.Index < this.History.Count:
                this.History[change.Index].UpdateFrom(change.HistoryItem);
                break;
            case AgentChatStateChangeKind.RunningAdded when change.RunningItem is not null:
                this.RunningItems.Add(this.CreateRunningItemViewModel(change.RunningItem));
                break;
            case AgentChatStateChangeKind.RunningUpdated when change.RunningItem is not null:
                this.RunningItems.FirstOrDefault(x => x.Source == change.RunningItem)?.UpdateModel();
                break;
            case AgentChatStateChangeKind.RunningRemoved when change.RunningItem is not null:
                var vm = this.RunningItems.FirstOrDefault(x => x.Source == change.RunningItem);
                if (vm is not null)
                {
                    this.RunningItems.Remove(vm);
                    vm.Dispose();
                }

                break;
            case AgentChatStateChangeKind.SessionChanged:
                if (!string.IsNullOrWhiteSpace(change.AgentSessionId))
                {
                    this.AgentSessionId = change.AgentSessionId;
                }

                break;
            case AgentChatStateChangeKind.Reset:
                this.ApplySnapshot(this.agentChat.GetStateSnapshot());
                break;
        }
    }

    private ChatHistoryItemViewModel CreateHistoryViewModel(AgentChatHistoryItem item)
    {
        var vm = new ChatHistoryItemViewModel(item);
        vm.SetReasoningVisible(this.IsReasoningVisible);
        return vm;
    }

    private RunningItemViewModel CreateRunningItemViewModel(AgentChatRunningItem item)
    {
        var vm = new RunningItemViewModel(item);
        vm.SetReasoningVisible(this.IsReasoningVisible);
        return vm;
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
        this.agentChat.StateChanged -= this.OnStateChanged;
        await this.agentChat.DisposeAsync();
    }
}
