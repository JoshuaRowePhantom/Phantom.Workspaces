namespace Phantom.Workspaces.Services.Notifications;

public sealed record NotificationTargetRequest
{
    public required string TabId { get; init; }
    public required string Kind { get; init; }
}
