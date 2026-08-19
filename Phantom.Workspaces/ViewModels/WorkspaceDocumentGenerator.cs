using System;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Core;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// ItemsSource container generator for <see cref="WorkspaceContentDock"/>.
/// Creates a <see cref="WorkspaceDocument"/> for each <see cref="WorkspaceTabViewModel"/>
/// in the dock's ItemsSource.
/// </summary>
public sealed class WorkspaceDocumentGenerator : DockItemContainerGenerator
{
    private readonly Func<string, WorkspaceDocument?>? getDocumentForTab;
    private readonly Action<WorkspaceDocument>? onPrepared;
    private readonly Action<string>? onCleared;

    public WorkspaceDocumentGenerator(
        Func<string, WorkspaceDocument?>? getDocumentForTab = null,
        Action<WorkspaceDocument>? onPrepared = null,
        Action<string>? onCleared = null)
    {
        this.getDocumentForTab = getDocumentForTab;
        this.onPrepared = onPrepared;
        this.onCleared = onCleared;
    }

    public override IDockable? CreateDocumentContainer(IItemsSourceDock dock, object item, int index)
    {
        if (item is not WorkspaceTabViewModel tab)
        {
            return null;
        }

        // #1333/#1341: if this tab already has a document hosted in a DIFFERENT region of THIS pane
        // (a restored non-primary split dock that was registered before this ItemsSource-bound
        // primary dock was populated), do not fabricate a duplicate wrapper here. Returning null
        // makes the Dock ItemsSource sync skip adding a container, leaving the tab in its own region
        // and keeping the owning pane's registry pointing at the dock that actually hosts it.
        // The lookup resolves against the OWNING PANE's registry, so it can no longer false-positive
        // against a stale entry left by a different, already-closed pane (#1340 mechanism (A)).
        var existing = this.getDocumentForTab?.Invoke(tab.Id);
        if (existing is not null && !ReferenceEquals(existing.Owner, dock))
        {
            return null;
        }

        return new WorkspaceDocument();
    }

    public override void PrepareDocumentContainer(IItemsSourceDock dock, IDockable container, object item, int index)
    {
        if (container is WorkspaceDocument doc && item is WorkspaceTabViewModel tab)
        {
            // #1335/#1334: wire the per-document header/status/notification services (via
            // Initialize) at document-creation time, BEFORE the document is registered with the
            // factory. Because Initialize runs first, documentsByTabId always receives the single
            // fully-wired instance for this tab Id — there is no last-writer-wins race between a
            // wired and an unwired duplicate, so GetDocumentForTab returns the currently-rendered,
            // header-configured document.
            doc.Initialize(tab);
            this.onPrepared?.Invoke(doc);
        }
    }

    public override void ClearDocumentContainer(IItemsSourceDock dock, IDockable container, object? item)
    {
        if (container is WorkspaceDocument doc)
        {
            this.onCleared?.Invoke(doc.Id);
        }
    }
}
