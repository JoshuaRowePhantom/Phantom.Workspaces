using System;
using System.Collections.Generic;

namespace Phantom.Workspaces.Services.Notifications;

public interface INotificationService
{
    void Notify(Notification notification);
    void Remove(string tabId);
    void MarkRead(string tabId);
    IReadOnlyList<NotificationEntry> Notifications { get; }
    bool HasActiveRun { get; }
    event EventHandler NotificationsChanged;
}
