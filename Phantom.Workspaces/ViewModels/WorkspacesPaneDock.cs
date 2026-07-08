using System.Collections;
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

    /// <summary>
    /// Shadows ItemsSource to prevent serializing the ObservableCollection<WorkspacePaneViewModel>,
    /// which is not JSON-serializable. Workspace panes are restored via descriptors and
    /// OpenWorkspaceAsync, not by serializing the runtime pane view models.
    /// </summary>
    [JsonIgnore]
    public new IEnumerable? ItemsSource
    {
        get => base.ItemsSource;
        set => base.ItemsSource = value;
    }

    /// <summary>
    /// Shadows ItemContainerGenerator to prevent serialization of the generator instance.
    /// </summary>
    [JsonIgnore]
    public new IDockItemContainerGenerator? ItemContainerGenerator
    {
        get => base.ItemContainerGenerator;
        set => base.ItemContainerGenerator = value;
    }
}
