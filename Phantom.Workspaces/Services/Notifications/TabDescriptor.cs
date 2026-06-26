namespace Phantom.Workspaces.Services.Notifications;

public sealed class TabDescriptor
{
    public required string TabId { get; init; }
    public string? WorkspaceId { get; init; }
}
