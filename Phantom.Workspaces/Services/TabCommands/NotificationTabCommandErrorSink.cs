using Phantom.Workspaces.Services.Notifications;

namespace Phantom.Workspaces.Services.TabCommands;

/// <summary>
/// Surfaces tab-command failures to the user through the application
/// <see cref="INotificationService"/> instead of silently swallowing them. The notification is
/// scoped to the failing tab so it never contaminates another tab's status.
/// </summary>
internal sealed class NotificationTabCommandErrorSink : ITabCommandErrorSink
{
    private readonly INotificationService notificationService;

    public NotificationTabCommandErrorSink(INotificationService notificationService)
    {
        ArgumentNullException.ThrowIfNull(notificationService);
        this.notificationService = notificationService;
    }

    public void ReportFailure(TabCommandContext context, TabCommandDescriptor command, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(exception);

        var tab = context.Tab;
        this.notificationService.Notify(new Notification(
            new TabDescriptor
            {
                TabId = tab.Id,
                TabTitle = tab.Title,
                WorkspaceId = context.Pane.Id,
            },
            $"{command.Header} failed",
            exception.Message,
            DateTime.UtcNow,
            RunningState.Idle,
            NotificationState.Interesting));
    }
}
