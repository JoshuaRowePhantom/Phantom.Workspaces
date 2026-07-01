using Dock.Model.Avalonia.Controls;
using Dock.Model.Core;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// A custom document dock for workspace-pane tabs (the outer workspace switcher).
/// Using a dedicated type allows providing a DataTemplate that explicitly sets
/// <see cref="Dock.Avalonia.Controls.DocumentControl.HeaderTemplate"/>, enabling
/// custom tab header rendering with running and notification indicators.
/// </summary>
public class WorkspacesPaneDock : DocumentDock
{
    /// <summary>
    /// Shadows the inherited [DataMember] Owner to break the serialization cycle
    /// (Owner → RootDock → VisibleDockables → WorkspacesPaneDock).
    /// </summary>
    [JsonIgnore]
    public new IDockable? Owner
    {
        get => base.Owner;
        set => base.Owner = value;
    }

    /// <summary>
    /// Shadows the Avalonia StyledElement.StyleKey (System.Type) which STJ cannot serialize.
    /// </summary>
    [JsonIgnore]
    public new Type? StyleKey => base.StyleKey;
}
