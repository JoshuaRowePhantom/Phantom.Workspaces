using System;
using System.Threading.Tasks;
using Phantom.Workspaces.Services.Notifications;

namespace Phantom.Workspaces.Services.Navigation;

/// <summary>
/// The single <see cref="ITabNavigator"/> implementation. Orchestrates the pane-selection /
/// tab-activation / entity-open primitives exposed by <see cref="ITabNavigatorHost"/>, together with
/// the navigation-history and notification services, so the brain button, the notifications dropdown,
/// and the Ctrl nav-stack popup all navigate identically. See issue #1254.
/// </summary>
internal sealed class MainWindowTabNavigator : ITabNavigator
{
    private readonly ITabNavigatorHost host;
    private readonly INavigationHistoryService history;
    private readonly INotificationService notifications;

    public MainWindowTabNavigator(
        ITabNavigatorHost host,
        INavigationHistoryService history,
        INotificationService notifications)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        this.notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
    }

    public async Task<bool> NavigateAsync(NavigationTarget target, NavigationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        options ??= new NavigationOptions();

        if (options.FocusWindow)
        {
            this.host.FocusMainWindow();
        }

        if (target.DocumentTabId is { } tabId)
        {
            // Reuses the existing #1157 open-but-unselected pane logic.
            await this.host.ActivateTabByRequestAsync(
                new NavigationRequest(target.WorkspaceTabId ?? string.Empty, tabId));

            if (options.MarkNotificationRead)
            {
                this.notifications.MarkRead(tabId);
            }

            if (options.PushHistory && !this.host.NavigatingViaHistory)
            {
                var paneId = target.WorkspaceTabId ?? this.host.SelectedWorkspacePaneId;
                if (paneId is not null)
                {
                    this.history.Push(new NavigationEntry(tabId, paneId));
                }
            }

            return true;
        }

        // Brain-button fallback: no tab open for this agent session.
        if (options.OpenEntityIfNoTab && target.AgentSessionKey is { } key)
        {
            await this.host.OpenAgentForSessionAsync(key);
            return true;
        }

        return false;
    }
}
