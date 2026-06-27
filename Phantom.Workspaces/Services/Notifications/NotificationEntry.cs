using System;

namespace Phantom.Workspaces.Services.Notifications;

public sealed record NotificationEntry
{
    public required string TabKey { get; init; }
    public required TabDescriptor TabDescriptor { get; init; }
    public required string? Reason { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required bool IsRead { get; init; }
    public required bool IsSnoozed { get; init; }
}
