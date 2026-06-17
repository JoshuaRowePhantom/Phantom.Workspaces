using Dock.Model.Mvvm.Controls;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Dock document wrapper for WorkspacePaneViewModel (workspace-level tab).
/// </summary>
public class WorkspacePaneDocument : Document
{
    public WorkspacePaneDocument(WorkspacePaneViewModel workspacePane)
    {
        this.WorkspacePane = workspacePane;
    }

    public WorkspacePaneViewModel WorkspacePane { get; }
}
