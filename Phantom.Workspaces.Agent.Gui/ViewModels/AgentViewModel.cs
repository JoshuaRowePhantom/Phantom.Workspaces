using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
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
        agentChat.ToolsChanged += this.OnToolsChanged;
        this.ApplySnapshot(agentChat.GetStateSnapshot());
        this.ApplyToolSnapshot(agentChat.GetToolSnapshot());
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

    public ObservableCollection<AgentChatToolViewModel> Tools { get; } = [];

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
                if (this.TryApplyRunningAssistantToPlaceholder(item))
                {
                    continue;
                }

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
                if (this.TryApplyRunningAssistantToPlaceholder(change.RunningItem))
                {
                    break;
                }

                this.RunningItems.Add(this.CreateRunningItemViewModel(change.RunningItem));
                break;
            case AgentChatStateChangeKind.RunningUpdated when change.RunningItem is not null:
                if (this.TryApplyRunningAssistantToPlaceholder(change.RunningItem))
                {
                    break;
                }

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

    private bool TryApplyRunningAssistantToPlaceholder(AgentChatRunningItem runningItem)
    {
        if (this.History.Count == 0)
        {
            return false;
        }

        var placeholder = this.History[^1];
        if (placeholder.Role != ChatRole.Assistant || !placeholder.IsInProgress)
        {
            return false;
        }

        var assistant = SelectLatestAssistantContent(runningItem.Items);
        if (assistant is null)
        {
            return false;
        }

        placeholder.UpdateFrom(assistant with { IsInProgress = true });
        return true;
    }

    private static AgentChatHistoryItem? SelectLatestAssistantContent(AgentChatHistoryItem[]? items)
    {
        if (items is not { Length: > 0 })
        {
            return null;
        }

        return items
            .LastOrDefault(static item =>
                item.Role == ChatRole.Assistant
                && (!string.IsNullOrWhiteSpace(item.Text) || !string.IsNullOrWhiteSpace(item.ReasoningText)))
            ?? items.LastOrDefault(static item => item.Role == ChatRole.Assistant);
    }

    private void OnToolsChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => this.ApplyToolSnapshot(this.agentChat.GetToolSnapshot()));

    private void ApplyToolSnapshot(IReadOnlyList<AgentChatToolItem> tools)
    {
        this.Tools.Clear();
        foreach (var tool in tools)
        {
            this.Tools.Add(this.CreateToolViewModel(tool));
        }
    }

    private AgentChatToolViewModel CreateToolViewModel(AgentChatToolItem tool)
        => new(
            tool.Id,
            tool.Name,
            tool.Description,
            tool.Kind,
            tool.IsEnabled,
            tool.Status,
            tool.Children.Select(this.CreateToolViewModel).ToArray(),
            enabled => this.agentChat.SetToolEnabledAsync(tool.Id, enabled));

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
        this.agentChat.ToolsChanged -= this.OnToolsChanged;
        await this.agentChat.DisposeAsync();
    }
}
