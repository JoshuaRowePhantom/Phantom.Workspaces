using System;
using System.Collections.Generic;

namespace Phantom.Workspaces.Services.Notifications;

public interface INotificationService
{
    void Notify(TabDescriptor tab, string? reason);
    void NotifyRunning(string tabId, bool isRunning);
    void MarkRead(string tabId);
    IReadOnlyList<NotificationEntry> Notifications { get; }
    bool HasActiveRun { get; }
    event EventHandler NotificationsChanged;
}
