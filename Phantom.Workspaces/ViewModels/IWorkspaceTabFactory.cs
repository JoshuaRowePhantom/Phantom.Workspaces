using System.Text.Json;
using System.Threading.Tasks;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// #1341: bundles the descriptor→tab and entity→tab creation services that
/// <see cref="WorkspacePaneViewModel"/> needs when populating or restoring its tabs, so the
/// per-pane populate/restore logic no longer reaches back into <see cref="MainWindowViewModel"/>'s
/// private fields (entity broker, catalogs, shortcut handlers). Implemented by
/// <see cref="MainWindowViewModel"/>, which owns the concrete tab-construction pipeline.
/// </summary>
public interface IWorkspaceTabFactory
{
    /// <summary>
    /// Recreates a tab view model from a persisted <see cref="DockTabDescriptor"/> (restore path).
    /// </summary>
    Task<WorkspaceTabViewModel?> CreateTabViewModelFromDescriptorAsync(
        SubscribedEntityViewModel workspaceEntity,
        DockTabDescriptor descriptor,
        string tabId);

    /// <summary>
    /// Recreates a tab view model from a persisted workspace-tab-descriptor JSON node (populate path).
    /// </summary>
    Task<WorkspaceTabViewModel?> TryFetchWorkspaceTabAsync(JsonElement tab);

    /// <summary>
    /// Creates the default entity tab shown when a workspace has no persisted tabs.
    /// </summary>
    WorkspaceTabViewModel CreateDefaultWorkspaceTab(SubscribedEntityViewModel workspaceEntity);
}
