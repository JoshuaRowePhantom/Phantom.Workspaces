namespace Phantom.Workspaces.Services.Navigation;

/// <summary>
/// Two-phase navigation payload. <see cref="WorkspaceTabId"/> identifies the workspace pane
/// (the <c>WorkspacePaneDocument</c> / <c>WorkspacePaneViewModel.Id</c>) that Phase-1 resolves;
/// <see cref="DocumentTabId"/> identifies the <c>WorkspaceDocument</c> tab id that Phase-2 resolves
/// against the owning pane's own tab→document registry. See #1341.
/// </summary>
public record NavigationRequest(string WorkspaceTabId, string DocumentTabId);
