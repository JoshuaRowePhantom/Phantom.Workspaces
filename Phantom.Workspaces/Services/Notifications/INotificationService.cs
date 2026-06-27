using System;
using System.Collections.Generic;

namespace Phantom.Workspaces.Services.Notifications;

public interface INotificationService
{
    void Notify(TabDescriptor tab, string? reason);
    void MarkRead(string tabId);
    IReadOnlyList<NotificationEntry> Notifications { get; }
    event EventHandler NotificationsChanged;
}
