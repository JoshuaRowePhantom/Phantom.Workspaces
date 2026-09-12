using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Gui.Shared.Utilities;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Remote;
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
    private bool isRestoring;
    private Task? restoreSettleTask;
    private long lastStreamingNotifyTicks;
    private const long StreamingThrottleMs = 500;
    private readonly StatusItem tabStatus = new();
    private readonly AsyncDisposableCollection leaseDisposables;
    private readonly TimeProvider timeProvider;
    private RunningAgentChatLease? lease;
    private AgentRunningIndicatorTabHeaderItemViewModel? runningIndicator;
    private bool isRemote;
    private string? remoteProfileDisplayName;
    private bool hasModalsNeedingInput;
    private bool disposed;

    public AgentSessionWorkspaceTabViewModel()
        : this(TimeProvider.System)
    {
    }

    internal AgentSessionWorkspaceTabViewModel(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.leaseDisposables = new AsyncDisposableCollection(this.OnLeaseDisposeError);
    }

    private void OnLeaseDisposeError(Exception ex)
        => this.loggerFactory?.CreateLogger<AgentSessionWorkspaceTabViewModel>()
            .LogError(ex, "Error disposing agent session lease.");

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

    public bool IsRemote
    {
        get => this.isRemote;
        private set => this.SetProperty(ref this.isRemote, value);
    }

    public string? RemoteProfileDisplayName
    {
        get => this.remoteProfileDisplayName;
        private set => this.SetProperty(ref this.remoteProfileDisplayName, value);
    }

    public bool HasModalsNeedingInput
    {
        get => this.hasModalsNeedingInput;
        private set => this.SetProperty(ref this.hasModalsNeedingInput, value);
    }

    /// <summary>
    /// The workspace-pane id the tab currently lives in.
    /// Init-stamped from the creating handler's <c>SelectedWorkspacePane?.Id</c>; overwritten
    /// authoritatively by <see cref="WorkspacePaneViewModel"/>'s Tabs.CollectionChanged handler
    /// when the tab is added to a pane so <see cref="CreateTabDescriptor"/> reflects the actual
    /// owning pane, not the pane that happened to be active at creation time (#1135).
    /// </summary>
    public RunningAgentChatLease? Lease => this.lease;

    public void SetLease(RunningAgentChatLease value)
    {
        this.lease = value;
        this.leaseDisposables.Add(value);
    }

    internal void SetRemoteProfileDisplayName(string? value)
    {
        if (this.State != AgentTabState.Loading || this.Agent is not null)
        {
            throw new InvalidOperationException(
                "Remote profile metadata must be staged before the tab becomes ready.");
        }

        this.remoteProfileDisplayName = value;
    }

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
        ArgumentNullException.ThrowIfNull(agentViewModel);
        ArgumentNullException.ThrowIfNull(factory);
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(AgentSessionWorkspaceTabViewModel));
        }
        if (this.State != AgentTabState.Loading || this.Agent is not null)
        {
            throw new InvalidOperationException("The agent session tab has already completed initialization.");
        }

        // Publish the ready surface as one coherent state transition. Consumers never observe a
        // Ready tab whose Agent and remote metadata describe different sessions.
        this.loggerFactory = factory;
        this.agent = agentViewModel;
        this.isRemote = agentViewModel.AgentChat is RemoteAgentChat or RemoteAgentChatProxy;
        if (!this.isRemote)
        {
            this.remoteProfileDisplayName = null;
        }
        this.hasModalsNeedingInput = agentViewModel.HasModalsNeedingInput;

        // #1451: open a restoring window before wiring up the notification handler. While the agent
        // rehydrates, running-state churn (persisted running items settling, and sub-agent
        // lease-acquisition continuations that run on the foreground scheduler after this method
        // returns) must update the running indicator but must not raise notifications.
        this.isRestoring = true;

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
        this.RaisePropertyChanged(nameof(this.Agent));
        this.RaisePropertyChanged(nameof(this.LoggerFactory));
        this.RaisePropertyChanged(nameof(this.IsRemote));
        this.RaisePropertyChanged(nameof(this.RemoteProfileDisplayName));
        this.RaisePropertyChanged(nameof(this.HasModalsNeedingInput));
        this.UpdateModalNotification(agentViewModel.HasModalsNeedingInput);

        // Close the restoring window once rehydration has fully settled, re-baselining wasRunning
        // from the settled state so the first observed transition is a genuine user edge.
        this.restoreSettleTask = this.CompleteRestoreWhenSettledAsync(agentViewModel);
    }

    private async Task CompleteRestoreWhenSettledAsync(AgentViewModel agentViewModel)
    {
        try
        {
            // ConfigureAwait(true): SetReady runs on the UI thread, so the re-baseline below is
            // serialized with OnAgentPropertyChanged (which is marshaled to the UI thread).
            await agentViewModel.RestoreSettled.ConfigureAwait(true);
        }
        catch
        {
            // Settle regardless — a rehydration failure must not leave the tab permanently muted.
        }

        var isRunning = agentViewModel.IsChatRunning;
        this.wasRunning = isRunning;
        this.tabStatus.RunningStatus = isRunning ? RunningStatus.Running : RunningStatus.Idle;
        if (this.runningIndicator is not null)
        {
            this.runningIndicator.IsRunning = isRunning;
        }

        this.isRestoring = false;
    }

    /// <summary>Test seam: completes once the restoring window has closed after <see cref="SetReady"/>.</summary>
    internal Task RestoreSettledTask => this.restoreSettleTask ?? Task.CompletedTask;

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

        this.UpdateModalNotification(hasModals: false);
        this.IsRemote = false;
        this.RemoteProfileDisplayName = null;
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
        if (this.disposed)
        {
            return;
        }

        if (sender is not AgentViewModel vm)
        {
            return;
        }

        if (e.PropertyName == nameof(AgentViewModel.HasModalsNeedingInput))
        {
            this.UpdateModalNotification(vm.HasModalsNeedingInput);
            return;
        }

        if (e.PropertyName != nameof(AgentViewModel.IsChatRunning))
        {
            return;
        }

        var isRunning = vm.IsChatRunning;
        this.tabStatus.RunningStatus = isRunning ? RunningStatus.Running : RunningStatus.Idle;
        if (this.runningIndicator is not null)
        {
            this.runningIndicator.IsRunning = isRunning;
        }

        // #1451: during rehydration, update the running indicator/status but suppress notifications.
        // The running-state churn observed here is restore-driven (persisted running items settling,
        // sub-agent lease-acquisition continuations), not a user-initiated run. wasRunning is
        // re-baselined from the settled state in CompleteRestoreWhenSettledAsync.
        if (this.isRestoring)
        {
            this.wasRunning = isRunning;
            return;
        }

        if (isRunning && !this.wasRunning)
        {
            this.lastStreamingNotifyTicks = Environment.TickCount64;
            var (textSummary, _) = AgentChatSummaryExtractor.ExtractRunning(vm.History, vm.RunningItems);
            this.NotificationService?.Notify(new Notification(
                this.CreateTabDescriptor(),
                "Running",
                textSummary ?? string.Empty,
                this.timeProvider.GetUtcNow().UtcDateTime,
                RunningState.Running,
                NotificationState.Interesting)
            {
                Kind = "chat-idle",
            });
        }
        else if (!isRunning && this.wasRunning)
        {
            var interrupted = IsInterrupted(vm);
            var heading = interrupted ? "Interrupted" : "Completed";
            var reason = interrupted ? "Interrupted" : BuildIdleReason(vm);
            this.NotificationService?.Notify(new Notification(
                this.CreateTabDescriptor(),
                heading,
                reason,
                this.timeProvider.GetUtcNow().UtcDateTime,
                RunningState.Idle,
                NotificationState.Interesting)
            {
                Kind = "chat-idle",
            });
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
                    this.CreateTabDescriptor(),
                    "Running",
                    textSummary ?? string.Empty,
                    this.timeProvider.GetUtcNow().UtcDateTime,
                    RunningState.Running,
                    NotificationState.NotInteresting)
                {
                    Kind = "chat-idle",
                });
            }
        }

        this.wasRunning = isRunning;
    }

    private void UpdateModalNotification(bool hasModals)
    {
        this.HasModalsNeedingInput = hasModals;
        if (this.NotificationService is null || string.IsNullOrWhiteSpace(this.Id))
        {
            return;
        }

        var target = new NotificationTargetRequest { TabId = this.Id, Kind = "modal-pending" };
        if (!hasModals)
        {
            this.NotificationService.Remove(target);
            return;
        }

        this.NotificationService.Notify(new Notification
        {
            TabDescriptor = this.CreateTabDescriptor(),
            Heading = "Input required",
            Description = "An agent is waiting for your response.",
            When = this.timeProvider.GetUtcNow().UtcDateTime,
            RunningState = this.Agent?.IsChatRunning == true ? RunningState.Running : RunningState.Idle,
            NotificationState = NotificationState.Interesting,
            Kind = "modal-pending",
        });
    }

    private void OnAgentAltKeyStateChanged(object? sender, bool isAltHeld)
    {
        this.RaiseAltKeyStateChanged(isAltHeld);
    }

    private TabDescriptor CreateTabDescriptor()
        => new()
        {
            TabId = this.Id,
            TabTitle = this.Title,
            WorkspaceId = this.WorkspacePaneId,
        };

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
        this.disposed = true;
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
                await this.leaseDisposables.DisposeAsync();
                this.lease = null;
            }
            else
            {
                await this.agent.DisposeAsync();
            }
        }
        else
        {
            await this.leaseDisposables.DisposeAsync();
            this.lease = null;
        }

        this.loggerFactory?.Dispose();
        await base.DisposeAsync();
    }
}
