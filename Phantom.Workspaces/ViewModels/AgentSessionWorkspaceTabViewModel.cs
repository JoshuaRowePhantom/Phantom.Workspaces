using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Notifications;

namespace Phantom.Workspaces.ViewModels;

public enum AgentTabState
{
    Loading,
    Ready,
    Failed,
}

public sealed class AgentSessionWorkspaceTabViewModel : WorkspaceTabViewModel
{
    private AgentTabState state = AgentTabState.Loading;
    private string? loadError;
    private AgentViewModel? agent;
    private ObservableLoggerFactory? loggerFactory;
    private bool wasRunning;
    private long lastStreamingNotifyTicks;
    private const long StreamingThrottleMs = 500;
    private readonly StatusItem tabStatus = new();
    private RunningAgentChatLease? lease;
    private AgentRunningIndicatorTabHeaderItemViewModel? runningIndicator;

    public AgentSessionWorkspaceTabViewModel()
    {
    }

    public AgentTabState State
    {
        get => this.state;
        private set => this.SetProperty(ref this.state, value);
    }

    public string? LoadError
    {
        get => this.loadError;
        private set => this.SetProperty(ref this.loadError, value);
    }

    public AgentViewModel? Agent
    {
        get => this.agent;
        private set => this.SetProperty(ref this.agent, value);
    }

    public ObservableLoggerFactory? LoggerFactory => this.loggerFactory;

    public INotificationService? NotificationService { get; init; }

    public string? AgentSessionId { get; init; }

    public RunningAgentChatLease? Lease => this.lease;

    public void SetLease(RunningAgentChatLease value) => this.lease = value;

    public event EventHandler<bool>? AltKeyStateChanged;
    public event EventHandler<int>? GoToTabAtIndexRequested;
    public event EventHandler<int>? GoToWorkspacePaneAtIndexRequested;

    public override IStatusItem TabStatus => this.tabStatus;

    public void RaiseAltKeyStateChanged(bool isAltHeld)
    {
        this.AltKeyStateChanged?.Invoke(this, isAltHeld);
    }

    public void RaiseGoToTabAtIndex(int index)
    {
        this.GoToTabAtIndexRequested?.Invoke(this, index);
    }

    public void RaiseGoToWorkspacePaneAtIndex(int index)
    {
        this.GoToWorkspacePaneAtIndexRequested?.Invoke(this, index);
    }

    public override void RequestFocusPrimaryControl()
    {
        base.RequestFocusPrimaryControl();
        this.agent?.InputQueue?.DefaultComposer.RequestFocusPrimaryControl();
    }

    public void SetReady(AgentViewModel agentViewModel, ObservableLoggerFactory factory)
    {
        this.loggerFactory = factory;
        this.Agent = agentViewModel;
        agentViewModel.PropertyChanged += this.OnAgentPropertyChanged;
        agentViewModel.AltKeyStateChanged += this.OnAgentAltKeyStateChanged;
        agentViewModel.GoToTabAtIndexRequested += this.OnAgentGoToTabAtIndexRequested;
        agentViewModel.GoToWorkspacePaneAtIndexRequested += this.OnAgentGoToWorkspacePaneAtIndexRequested;
        this.tabStatus.RunningStatus = agentViewModel.IsChatRunning ? RunningStatus.Running : RunningStatus.Idle;
        this.wasRunning = agentViewModel.IsChatRunning;

        this.runningIndicator = new AgentRunningIndicatorTabHeaderItemViewModel
        {
            IsRunning = agentViewModel.IsChatRunning,
        };
        var notificationIndicator = new NotificationIndicatorTabHeaderItemViewModel();
        var header = new TabHeaderViewModel { Title = this.Title };
        header.Items.Add(this.runningIndicator);
        header.Items.Add(notificationIndicator);
        this.TabHeader = header;

        this.State = AgentTabState.Ready;
    }

    /// <summary>
    /// Detaches and disposes the current agent and logger so the tab can be re-initialized
    /// with a new agent (e.g. after a /working-directory change).
    /// </summary>
    internal async Task ResetForRecreationAsync()
    {
        if (this.agent is not null)
        {
            this.agent.PropertyChanged -= this.OnAgentPropertyChanged;
            this.agent.AltKeyStateChanged -= this.OnAgentAltKeyStateChanged;
            this.agent.GoToTabAtIndexRequested -= this.OnAgentGoToTabAtIndexRequested;
            this.agent.GoToWorkspacePaneAtIndexRequested -= this.OnAgentGoToWorkspacePaneAtIndexRequested;
            if (this.lease is not null)
            {
                await this.agent.DisposeViewResourcesAsync();
                await this.lease.DisposeAsync();
                this.lease = null;
            }
            else
            {
                await this.agent.DisposeAsync();
            }
            this.Agent = null;
        }

        this.loggerFactory?.Dispose();
        this.loggerFactory = null;
        this.runningIndicator = null;
        this.TabHeader = null;
        this.State = AgentTabState.Loading;
    }

    public void SetFailed(string error)
    {
        this.LoadError = error;
        this.State = AgentTabState.Failed;
    }

    private void OnAgentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AgentViewModel.IsChatRunning) || sender is not AgentViewModel vm)
        {
            return;
        }

        var isRunning = vm.IsChatRunning;
        this.tabStatus.RunningStatus = isRunning ? RunningStatus.Running : RunningStatus.Idle;
        if (this.runningIndicator is not null)
        {
            this.runningIndicator.IsRunning = isRunning;
        }

        if (isRunning && !this.wasRunning)
        {
            this.lastStreamingNotifyTicks = Environment.TickCount64;
            var (textSummary, _) = AgentChatSummaryExtractor.ExtractRunning(vm.History, vm.RunningItems);
            this.NotificationService?.Notify(new Notification(
                new TabDescriptor { TabId = this.Id, TabTitle = this.Title },
                "Running",
                textSummary ?? string.Empty,
                DateTime.UtcNow,
                RunningState.Running,
                NotificationState.Interesting));
        }
        else if (!isRunning && this.wasRunning)
        {
            var interrupted = IsInterrupted(vm);
            var heading = interrupted ? "Interrupted" : "Completed";
            var reason = interrupted ? "Interrupted" : BuildIdleReason(vm);
            this.NotificationService?.Notify(new Notification(
                new TabDescriptor { TabId = this.Id, TabTitle = this.Title },
                heading,
                reason,
                DateTime.UtcNow,
                RunningState.Idle,
                NotificationState.Interesting));
        }
        else if (isRunning)
        {
            // Throttled streaming update
            var now = Environment.TickCount64;
            if (now - this.lastStreamingNotifyTicks >= StreamingThrottleMs)
            {
                this.lastStreamingNotifyTicks = now;
                var (textSummary, _) = AgentChatSummaryExtractor.ExtractRunning(vm.History, vm.RunningItems);
                this.NotificationService?.Notify(new Notification(
                    new TabDescriptor { TabId = this.Id, TabTitle = this.Title },
                    "Running",
                    textSummary ?? string.Empty,
                    DateTime.UtcNow,
                    RunningState.Running,
                    NotificationState.NotInteresting));
            }
        }

        this.wasRunning = isRunning;
    }

    private void OnAgentAltKeyStateChanged(object? sender, bool isAltHeld)
    {
        this.RaiseAltKeyStateChanged(isAltHeld);
    }

    private void OnAgentGoToTabAtIndexRequested(object? sender, int index)
    {
        this.RaiseGoToTabAtIndex(index);
    }

    private void OnAgentGoToWorkspacePaneAtIndexRequested(object? sender, int index)
    {
        this.RaiseGoToWorkspacePaneAtIndex(index);
    }

    private static bool IsInterrupted(AgentViewModel vm)
    {
        var lastItem = vm.History.LastOrDefault();
        return lastItem?.Role == AgentChatHistoryItem.DiagnosticChatRole
            && lastItem.Contents.OfType<TextContent>().Any(
                c => c.Text?.Contains("Interrupted by user.", StringComparison.OrdinalIgnoreCase) == true);
    }

    internal static string BuildIdleReason(AgentViewModel vm)
    {
        for (var i = vm.History.Count - 1; i >= 0; i--)
        {
            var item = vm.History[i];
            if (item.Role != ChatRole.Assistant)
            {
                continue;
            }

            var text = string.Concat(item.Contents.OfType<TextContent>().Select(c => c.Text ?? string.Empty)).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Length > 120 ? text[..120] + "\u2026" : text;
            }

            // Assistant message exists but has no text — check for tool calls.
            var toolName = item.Contents.OfType<FunctionCallContent>().FirstOrDefault()?.Name;
            if (toolName is not null)
            {
                return $"Completed \u2014 last action: {toolName}";
            }
        }

        return "Agent run completed.";
    }

    public override async ValueTask DisposeAsync()
    {
        if (this.agent is not null)
        {
            this.agent.PropertyChanged -= this.OnAgentPropertyChanged;
            this.agent.AltKeyStateChanged -= this.OnAgentAltKeyStateChanged;
            this.agent.GoToTabAtIndexRequested -= this.OnAgentGoToTabAtIndexRequested;
            this.agent.GoToWorkspacePaneAtIndexRequested -= this.OnAgentGoToWorkspacePaneAtIndexRequested;
            this.NotificationService?.Remove(this.Id);
            if (this.lease is not null)
            {
                await this.agent.DisposeViewResourcesAsync();
                await this.lease.DisposeAsync();
            }
            else
            {
                await this.agent.DisposeAsync();
            }
        }

        this.loggerFactory?.Dispose();
        await base.DisposeAsync();
    }
}
