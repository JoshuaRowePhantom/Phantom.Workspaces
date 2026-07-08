using System.Collections;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Core;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// A custom document dock for workspace content tabs.
/// Using a dedicated type allows providing a DataTemplate that explicitly sets
/// <see cref="Dock.Avalonia.Controls.DocumentControl.HeaderTemplate"/>, enabling
/// custom tab header rendering with icons and notifications.
/// </summary>
public class WorkspaceContentDock : DocumentDock
{
    /// <summary>
    /// Shadows the inherited [DataMember] Owner to break the serialization cycle
    /// (Owner → RootDock → VisibleDockables → ContentDock).
    /// </summary>
    [JsonIgnore]
    public new IDockable? Owner
    {
        get => base.Owner;
        set => base.Owner = value;
    }

    /// <summary>
    /// Shadows the Avalonia StyledElement.StyleKey (System.Type) which STJ cannot serialize.
    /// The shadow has no [JsonPropertyName] on the base, so [JsonIgnore] takes full effect.
    /// </summary>
    [JsonIgnore]
    public new Type? StyleKey => base.StyleKey;

    /// <summary>
    /// Shadows ItemsSource to prevent serializing the ObservableCollection<WorkspaceTabViewModel>,
    /// which is not JSON-serializable. The dock layout should be restored via descriptors, not
    /// by serializing the runtime tab view models.
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

    /// <summary>
    /// Adds the document to the dock. If there is already an active document, the new document
    /// is added without stealing focus. This prevents background population tasks (e.g.
    /// <c>PopulateWorkspacePaneTabsAsync</c> adding a default entity-view tab) from displacing
    /// a tab that was explicitly opened by the user concurrently.
    /// Callers that want to activate the new document (e.g. <c>OpenTabAsync</c>) do so
    /// explicitly via <c>SetActiveDockable</c> after calling <c>Tabs.Add</c>.
    /// </summary>
    public override void AddDocument(IDockable document)
    {
        if (ActiveDockable is not null)
        {
            Factory?.AddDockable(this, document);
        }
        else
        {
            base.AddDocument(document);
        }
    }
}
