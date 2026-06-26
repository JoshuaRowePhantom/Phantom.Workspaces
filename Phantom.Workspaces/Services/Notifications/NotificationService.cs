using System;
using System.Collections.Generic;

namespace Phantom.Workspaces.Services.Notifications;

public sealed class NotificationService : INotificationService
{
    private readonly IActiveTabProvider activeTabProvider;
    private readonly List<NotificationEntry> notifications = [];
    // TODO: Persist snoozed tab IDs to user-computer-profile entity under "snoozed-notification-tabs" field.
    private readonly HashSet<string> snoozedTabIds = [];

    public NotificationService(IActiveTabProvider activeTabProvider)
    {
        this.activeTabProvider = activeTabProvider ?? throw new ArgumentNullException(nameof(activeTabProvider));
    }

    public event EventHandler? NotificationsChanged;

    public IReadOnlyList<NotificationEntry> Notifications => this.notifications;

    public void Notify(TabDescriptor tab, string? reason)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var tabKey = tab.TabId;

        if (reason is null)
        {
            var removed = this.notifications.RemoveAll(e => e.TabKey == tabKey) > 0;
            if (removed)
            {
                this.NotificationsChanged?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        var isSnoozed = this.snoozedTabIds.Contains(tabKey);
        var isRead = isSnoozed || string.Equals(this.activeTabProvider.ActiveTabId, tab.TabId, StringComparison.Ordinal);

        var entry = new NotificationEntry
        {
            TabKey = tabKey,
            TabDescriptor = tab,
            Reason = reason,
            Timestamp = DateTimeOffset.UtcNow,
            IsRead = isRead,
            IsSnoozed = isSnoozed,
        };

        var existingIndex = this.notifications.FindIndex(e => e.TabKey == tabKey);
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
