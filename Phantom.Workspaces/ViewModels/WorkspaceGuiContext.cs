namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Host-provided context for the workspace-gui toolset. Gives AI tools programmatic access
/// to the main window and shortcut system so they can open/close workspace panes, close tabs,
/// and invoke entity shortcuts on the UI thread.
/// </summary>
public sealed record WorkspaceGuiContext
{
    /// <summary>The main window view model; all UI operations must be dispatched on its scheduler.</summary>
    public required MainWindowViewModel MainWindowViewModel { get; init; }

    /// <summary>The shortcut manager used to resolve and invoke shortcuts.</summary>
    public required ShortcutManager ShortcutManager { get; init; }
}
