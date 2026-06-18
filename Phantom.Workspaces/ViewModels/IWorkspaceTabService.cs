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
}
