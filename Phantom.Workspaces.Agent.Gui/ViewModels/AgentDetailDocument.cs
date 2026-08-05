using Dock.Model.Mvvm.Controls;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>
/// A cached document hosting a single agent-chat nav node's <c>DetailContent</c> (issue #1035).
/// One document is generated per <see cref="AgentDetailDocumentItem"/> in the detail dock's
/// <c>ItemsSource</c>; the cache-N/show-one dock keeps all of them alive and shows the active one.
/// </summary>
public sealed class AgentDetailDocument : Document
{
    /// <summary>The source item wired by <see cref="AgentDetailDocumentGenerator"/>.</summary>
    public AgentDetailDocumentItem? Item => base.Context as AgentDetailDocumentItem;

    /// <summary>The detail view-model rendered by the cached-content template.</summary>
    public object? DetailContent => (base.Context as AgentDetailDocumentItem)?.Content;

    /// <summary>
    /// Wires this document to its source item and freezes all docking interactions so the detail
    /// dock stays locked (no close/float/drag/drop/pin).
    /// </summary>
    internal void Initialize(AgentDetailDocumentItem item)
    {
        base.Context = item;
        this.Id = item.Key;
        this.Title = item.Title;
        this.CanClose = false;
        this.CanFloat = false;
        this.CanDrag = false;
        this.CanDrop = false;
        this.CanPin = false;
        this.CanDockAsDocument = false;
    }
}
