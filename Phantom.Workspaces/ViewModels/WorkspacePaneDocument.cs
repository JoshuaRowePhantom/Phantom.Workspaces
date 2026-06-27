using Dock.Model.Mvvm.Controls;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Dock document wrapper for WorkspacePaneViewModel (workspace-level tab).
/// </summary>
public class WorkspacePaneDocument : Document
{
    private readonly TabHeaderViewModel cachedTabHeader;

    public WorkspacePaneDocument(WorkspacePaneViewModel workspacePane)
    {
        this.WorkspacePane = workspacePane;
        this.cachedTabHeader = new TabHeaderViewModel { Title = workspacePane.Title };
    }

    public WorkspacePaneViewModel WorkspacePane { get; }

    /// <summary>
    /// Header model for this workspace-pane tab. Contains no items (title only),
    /// ensuring the shared <see cref="TabHeaderViewModel"/> DataTemplate applies correctly.
    /// </summary>
    public TabHeaderViewModel EffectiveTabHeader => this.cachedTabHeader;
}
