using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Services.TabCommands;

internal sealed class AgentSessionTabCommandProvider : ITabCommandProvider
{
    internal static AgentSessionTabCommandProvider Instance { get; } = new();

    private static readonly IReadOnlyList<TabCommandDescriptor> NoCommands = [];
    private static readonly IReadOnlyList<TabCommandDescriptor> RestartCommand =
    [
        new TabCommandDescriptor
        {
            Id = "agent-session.restart",
            Header = "Restart agent session…",
            AccessibleLabel = "Restart agent session",
            Group = "lifecycle",
            Order = 100,
            Availability = context => GetAvailability(context),
            ExecuteAsync = ExecuteRestartAsync,
        },
    ];

    private AgentSessionTabCommandProvider()
    {
    }

    public event EventHandler? CommandsChanged;

    public IReadOnlyList<TabCommandDescriptor> GetCommands(TabCommandContext context)
        => context.Tab is AgentSessionWorkspaceTabViewModel ? RestartCommand : NoCommands;

    internal void NotifyCommandsChanged() => this.CommandsChanged?.Invoke(this, EventArgs.Empty);

    private static TabCommandAvailability GetAvailability(TabCommandContext context)
    {
        if (context.Tab is not AgentSessionWorkspaceTabViewModel sessionTab)
        {
            return TabCommandAvailability.Hidden;
        }

        if (sessionTab.IsRestartInProgress)
        {
            return TabCommandAvailability.Disabled("Restart is already in progress for this session.");
        }

        if (string.IsNullOrWhiteSpace(sessionTab.AgentSessionId))
        {
            return TabCommandAvailability.Disabled("Session startup is still in progress.");
        }

        if (sessionTab.Lease is null && sessionTab.Agent is null)
        {
            return TabCommandAvailability.Disabled("Session runtime is not ready.");
        }

        if (context.MainWindow is null)
        {
            return TabCommandAvailability.Disabled("Restart is unavailable in this context.");
        }

        var restartAvailability = context.MainWindow.AgentSessionRestartService.GetAvailability(sessionTab, context);
        return restartAvailability.IsAvailable
            ? TabCommandAvailability.Enabled
            : TabCommandAvailability.Disabled(
                restartAvailability.DisabledReason ?? "Restart is unavailable for this session.");
    }

    private static async Task<TabCommandResult> ExecuteRestartAsync(
        TabCommandContext context,
        CancellationToken cancellationToken)
    {
        if (context.Tab is not AgentSessionWorkspaceTabViewModel sessionTab || context.MainWindow is null)
        {
            return TabCommandResult.Rejected("Restart is unavailable in this context.");
        }

        var sessionId = sessionTab.AgentSessionId;
        var expectedLease = sessionTab.Lease;
        var expectedChat = expectedLease?.AgentChat ?? sessionTab.Agent?.AgentChat;
        if (string.IsNullOrWhiteSpace(sessionId) || expectedChat is null)
        {
            return TabCommandResult.Rejected("Session runtime is not available for restart.");
        }

        if (AgentSessionCloseActivity.HasPendingWork(expectedChat))
        {
            var consent = await context.MainWindow.AgentSessionRestartConsentProvider
                .ConfirmAsync(sessionTab.Title, cancellationToken)
                .ConfigureAwait(false);
            if (!consent)
            {
                return TabCommandResult.Cancelled();
            }
        }

        if (!context.IsStillValid()
            || !ReferenceEquals(sessionTab.Lease, expectedLease)
            || !ReferenceEquals(sessionTab.Lease?.AgentChat ?? sessionTab.Agent?.AgentChat, expectedChat))
        {
            sessionTab.SetRestartError(
                "Restart cancelled because the target tab or runtime changed while confirmation was open.");
            return TabCommandResult.Rejected("Target changed while restart was pending.");
        }

        sessionTab.SetRestartError(null);
        sessionTab.SetRestartInProgress(true);
        try
        {
            var result = await context.MainWindow.AgentSessionRestartService.RestartAsync(
                    new AgentSessionRestartRequest
                    {
                        SessionId = sessionId,
                        ExpectedChat = expectedChat,
                        ExpectedLease = expectedLease,
                        CommandId = Guid.NewGuid(),
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            return result.Outcome switch
            {
                AgentSessionRestartOutcome.Succeeded => TabCommandResult.Success(),
                AgentSessionRestartOutcome.Cancelled => TabCommandResult.Cancelled(),
                AgentSessionRestartOutcome.TargetChanged => SetRejected(
                    result.Message ?? "Restart cancelled because the target changed."),
                AgentSessionRestartOutcome.Failed => SetRejected(
                    result.Message ?? "Restart failed."),
                _ => SetRejected("Restart failed."),
            };
        }
        finally
        {
            sessionTab.SetRestartInProgress(false);
        }

        TabCommandResult SetRejected(string message)
        {
            sessionTab.SetRestartError(message);
            return TabCommandResult.Rejected(message);
        }
    }
}
