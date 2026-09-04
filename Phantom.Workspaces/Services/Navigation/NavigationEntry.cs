namespace Phantom.Workspaces.Services.Navigation;

/// <summary>
/// A single entry on the navigation history stack. <see cref="DocumentTabId"/> is the
/// <c>WorkspaceDocument</c> tab id; <see cref="WorkspaceTabId"/> is an optional hint for the
/// workspace pane (<c>WorkspacePaneViewModel.Id</c>) that owns the tab. Renamed from
/// <c>TabId</c>/<c>WorkspacePaneId</c> for clarity while keeping identical semantics (tab id, pane id).
/// The history is held purely in memory (see <see cref="NavigationHistoryService"/>), so there is no
/// on-disk representation to preserve. See #1341.
/// </summary>
public sealed record NavigationEntry(string DocumentTabId, string? WorkspaceTabId);
