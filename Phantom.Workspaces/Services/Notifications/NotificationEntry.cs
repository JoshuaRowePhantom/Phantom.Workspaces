using System;

namespace Phantom.Workspaces.Services.Notifications;

public sealed record NotificationEntry
{
    public required string TabKey { get; init; }
    public required TabDescriptor TabDescriptor { get; init; }
    public required string Heading { get; init; }
    public required string Description { get; init; }
    public required DateTime When { get; init; }
    public required bool IsRunning { get; init; }
    public required bool IsInteresting { get; init; }
    public required bool IsRead { get; init; }
    public required bool IsSnoozed { get; init; }
}
