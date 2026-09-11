using System;
using System.Diagnostics.CodeAnalysis;

namespace Phantom.Workspaces.Services.Notifications;

public record Notification
{
    public Notification()
    {
    }

    [SetsRequiredMembers]
    public Notification(
        TabDescriptor tabDescriptor,
        string heading,
        string description,
        DateTime when,
        RunningState runningState,
        NotificationState notificationState)
    {
        this.TabDescriptor = tabDescriptor;
        this.Heading = heading;
        this.Description = description;
        this.When = when;
        this.RunningState = runningState;
        this.NotificationState = notificationState;
    }

    public required TabDescriptor TabDescriptor { get; init; }
    public required string Heading { get; init; }
    public required string Description { get; init; }
    public required DateTime When { get; init; }
    public required RunningState RunningState { get; init; }
    public required NotificationState NotificationState { get; init; }
    public string Kind { get; init; } = "legacy";
}

public enum RunningState { Idle, Running }

public enum NotificationState { NotInteresting, Interesting }
