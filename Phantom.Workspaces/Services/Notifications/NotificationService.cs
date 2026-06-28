using System;
using System.Collections.Generic;
using System.Linq;

namespace Phantom.Workspaces.Services.Notifications;

public sealed class NotificationService : INotificationService
{
    private readonly IActiveTabProvider activeTabProvider;
    private readonly List<NotificationEntry> notifications = [];
    // Snooze state is intentionally ephemeral (in-memory only); it resets on restart.
    private readonly HashSet<string> snoozedTabIds = [];

    public NotificationService(IActiveTabProvider activeTabProvider)
    {
        this.activeTabProvider = activeTabProvider ?? throw new ArgumentNullException(nameof(activeTabProvider));
    }

    public event EventHandler? NotificationsChanged;

    public IReadOnlyList<NotificationEntry> Notifications => this.notifications;

    public bool HasActiveRun => this.notifications.Any(e => e.IsRunning);

    public void Notify(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var tabKey = notification.TabDescriptor.TabId;
        var isSnoozed = this.snoozedTabIds.Contains(tabKey);

        var existingIndex = this.notifications.FindIndex(e => e.TabKey == tabKey);

        bool isRead;
        if (notification.NotificationState == NotificationState.NotInteresting)
        {
            // Silent update: preserve existing IsRead so popup does not reopen.
            isRead = existingIndex >= 0 ? this.notifications[existingIndex].IsRead : true;
        }
        else
        {
            isRead = isSnoozed || string.Equals(
                this.activeTabProvider.ActiveTabId,
                notification.TabDescriptor.TabId,
                StringComparison.Ordinal);
        }

        var entry = new NotificationEntry
        {
            TabKey = tabKey,
            TabDescriptor = notification.TabDescriptor,
            Heading = notification.Heading,
            Description = notification.Description,
            When = notification.When,
            IsRunning = notification.RunningState == RunningState.Running,
            IsInteresting = notification.NotificationState == NotificationState.Interesting,
            IsRead = isRead,
            IsSnoozed = isSnoozed,
        };

        if (existingIndex >= 0)
        {
            this.notifications[existingIndex] = entry;
        }
        else
        {
            this.notifications.Add(entry);
        }

        this.NotificationsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string tabId)
    {
        ArgumentNullException.ThrowIfNull(tabId);
        var removed = this.notifications.RemoveAll(e => e.TabKey == tabId) > 0;
        if (removed)
        {
            this.NotificationsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void MarkRead(string tabId)
    {
        ArgumentNullException.ThrowIfNull(tabId);
        var index = this.notifications.FindIndex(e => e.TabKey == tabId);
        if (index < 0 || this.notifications[index].IsRead)
        {
            return;
        }

        this.notifications[index] = this.notifications[index] with { IsRead = true };
        this.NotificationsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SnoozeTab(string tabId)
    {
        ArgumentNullException.ThrowIfNull(tabId);
        this.snoozedTabIds.Add(tabId);

        var index = this.notifications.FindIndex(e => e.TabKey == tabId);
        if (index >= 0)
        {
            this.notifications[index] = this.notifications[index] with { IsRead = true, IsSnoozed = true };
        }

        this.NotificationsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UnsnoozeTab(string tabId)
    {
        ArgumentNullException.ThrowIfNull(tabId);
        if (!this.snoozedTabIds.Remove(tabId))
        {
            return;
        }

        var index = this.notifications.FindIndex(e => e.TabKey == tabId);
        if (index >= 0)
        {
            this.notifications[index] = this.notifications[index] with { IsSnoozed = false };
        }

        this.NotificationsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsTabSnoozed(string tabId) => this.snoozedTabIds.Contains(tabId);
}
