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
    Task OpenTabAsync(WorkspaceTabViewModel tab, string? insertAfterTabId = null, bool focus = true);

    /// <summary>
    /// Replaces an existing workspace tab with a new one.
    /// </summary>
    Task ReplaceTabAsync(WorkspaceTabViewModel oldTab, WorkspaceTabViewModel newTab);

    /// <summary>
    /// Closes the specified tab and disposes it.
    /// </summary>
    void CloseTab(WorkspaceTabViewModel tab);
}
