using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Service for opening workspace tabs.
/// </summary>
public interface IWorkspaceTabService
{
    /// <summary>
    /// Opens a new workspace tab.
    /// </summary>
    /// <param name="tab">The tab view model to open.</param>
    /// <param name="insertAfterTabId">
    /// When set, the new tab is inserted immediately to the right of the tab with this id.
    /// If the id is not found the tab is appended at the end.
    /// </param>
    /// <param name="focus">Whether to activate and focus the tab. Defaults to <see langword="true"/>.</param>
    /// <param name="workspacePaneId">
    /// When set, the tab is opened in the workspace pane with this id instead of the currently
    /// selected pane. Falls back to the selected pane if no matching pane is found.
    /// </param>
    Task OpenTabAsync(WorkspaceTabViewModel tab, string? insertAfterTabId = null, bool focus = true, string? workspacePaneId = null);

    /// <summary>
    /// Replaces an existing workspace tab with a new one.
    /// </summary>
    Task ReplaceTabAsync(WorkspaceTabViewModel oldTab, WorkspaceTabViewModel newTab);

    /// <summary>
    /// Closes the specified tab and disposes it.
    /// </summary>
    void CloseTab(WorkspaceTabViewModel tab);
}
