using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services.Notifications;

namespace Phantom.Workspaces.ViewModels;

public enum AgentTabState
{
    Loading,
    Ready,
    Failed,
}

public sealed class AgentSessionWorkspaceTabViewModel : WorkspaceTabViewModel, IAsyncDisposable
{
    private AgentTabState state = AgentTabState.Loading;
    private string? loadError;
    private AgentViewModel? agent;
    private ObservableLoggerFactory? loggerFactory;
    private bool wasRunning;
    private readonly AgentRunningIndicatorTabHeaderItemViewModel agentRunningIndicator;

    public AgentSessionWorkspaceTabViewModel()
    {
        this.agentRunningIndicator = new AgentRunningIndicatorTabHeaderItemViewModel();
        var header = new TabHeaderViewModel { Title = string.Empty };
        header.Items.Add(this.agentRunningIndicator);
        this.TabHeader = header;
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

    public void SetReady(AgentViewModel agentViewModel, ObservableLoggerFactory factory)
    {
        this.loggerFactory = factory;
        this.Agent = agentViewModel;
        agentViewModel.PropertyChanged += this.OnAgentPropertyChanged;
        this.agentRunningIndicator.IsRunning = agentViewModel.IsChatRunning;
        this.wasRunning = agentViewModel.IsChatRunning;
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
            await this.agent.DisposeAsync();
            this.Agent = null;
        }

        this.loggerFactory?.Dispose();
        this.loggerFactory = null;
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
        this.agentRunningIndicator.IsRunning = isRunning;
        this.NotificationService?.NotifyRunning(this.Id, isRunning);

        if (isRunning && !this.wasRunning)
        {
            this.NotificationService?.Notify(new TabDescriptor { TabId = this.Id, TabTitle = this.Title }, "Run started");
        }
        else if (!isRunning && this.wasRunning)
        {
            var reason = IsInterrupted(vm) ? "Interrupted" : BuildIdleReason(vm);
            this.NotificationService?.Notify(new TabDescriptor { TabId = this.Id, TabTitle = this.Title }, reason);
        }

        this.wasRunning = isRunning;
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

    public async ValueTask DisposeAsync()
    {
        if (this.agent is not null)
        {
            this.agent.PropertyChanged -= this.OnAgentPropertyChanged;
            this.NotificationService?.NotifyRunning(this.Id, false);
            this.NotificationService?.Notify(new TabDescriptor { TabId = this.Id, TabTitle = this.Title }, null);
            await this.agent.DisposeAsync();
        }

        this.loggerFactory?.Dispose();
    }
}
