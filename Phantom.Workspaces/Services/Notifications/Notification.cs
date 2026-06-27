using System;

namespace Phantom.Workspaces.Services.Notifications;

public record Notification(
    TabDescriptor TabDescriptor,
    string Heading,
    string Description,
    DateTime When,
    RunningState RunningState,
    NotificationState NotificationState);

public enum RunningState { Idle, Running }

public enum NotificationState { NotInteresting, Interesting }
