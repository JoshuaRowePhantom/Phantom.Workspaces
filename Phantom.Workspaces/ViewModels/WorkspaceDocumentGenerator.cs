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
    private readonly WorkspaceDockFactory? factory;
    private readonly Action<WorkspaceDocument>? onPrepared;
    private readonly Action<string>? onCleared;

    public WorkspaceDocumentGenerator(
        WorkspaceDockFactory? factory = null,
        Action<WorkspaceDocument>? onPrepared = null,
        Action<string>? onCleared = null)
    {
        this.factory = factory;
        this.onPrepared = onPrepared;
        this.onCleared = onCleared;
    }

    public override IDockable? CreateDocumentContainer(IItemsSourceDock dock, object item, int index)
    {
        if (item is not WorkspaceTabViewModel tab)
        {
            return null;
        }

        // #1333: if this tab already has a document hosted in a DIFFERENT region (a restored
        // non-primary split dock that was registered before this ItemsSource-bound primary dock
        // was populated), do not fabricate a duplicate wrapper here. Returning null makes the
        // Dock ItemsSource sync skip adding a container, leaving the tab in its own region and
        // keeping documentsByTabId pointing at the dock that actually hosts it.
        var existing = this.factory?.GetDocumentForTab(tab.Id);
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
