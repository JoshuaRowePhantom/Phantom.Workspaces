namespace Phantom.Workspaces.ViewModels;

public enum WorkspaceStartupState
{
    PreparingShell,
    ShellPrepared,
    LoadingWorkspaces,
    Ready,
    Failed,
    Cancelled,
}
