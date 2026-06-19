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
    Task OpenTabAsync(WorkspaceTabViewModel tab);

    /// <summary>
    /// Replaces an existing workspace tab with a new one.
    /// </summary>
    Task ReplaceTabAsync(WorkspaceTabViewModel oldTab, WorkspaceTabViewModel newTab);
}
