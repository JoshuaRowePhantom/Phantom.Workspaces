namespace Phantom.Workspaces.Services.Navigation;

/// <summary>
/// Two-phase navigation payload carrying one complete pane + tab identity.
/// </summary>
public sealed record NavigationRequest(UiPath Path);
