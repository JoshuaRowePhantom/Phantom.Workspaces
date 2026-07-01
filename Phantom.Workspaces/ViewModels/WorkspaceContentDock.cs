using Dock.Model.Avalonia.Controls;
using Dock.Model.Core;

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
