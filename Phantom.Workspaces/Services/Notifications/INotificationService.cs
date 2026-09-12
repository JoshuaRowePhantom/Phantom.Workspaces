using System;
using System.Collections.Generic;

namespace Phantom.Workspaces.Services.Notifications;

public interface INotificationService
{
    void Notify(Notification notification);
    void Remove(string tabId);
    void Remove(NotificationTargetRequest request);
    void MarkRead(string tabId);
    void MarkRead(NotificationTargetRequest request);
    IReadOnlyList<NotificationEntry> Notifications { get; }
    bool HasActiveRun { get; }
    event EventHandler NotificationsChanged;
}
