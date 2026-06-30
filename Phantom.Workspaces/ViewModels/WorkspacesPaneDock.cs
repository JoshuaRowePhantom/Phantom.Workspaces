using Dock.Model.Avalonia.Controls;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// A custom document dock for workspace-pane tabs (the outer workspace switcher).
/// Using a dedicated type allows providing a DataTemplate that explicitly sets
/// <see cref="Dock.Avalonia.Controls.DocumentControl.HeaderTemplate"/>, enabling
/// custom tab header rendering with running and notification indicators.
/// </summary>
public class WorkspacesPaneDock : DocumentDock
{
}
