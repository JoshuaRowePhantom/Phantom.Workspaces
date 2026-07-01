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
    private readonly Action<WorkspaceDocument>? onPrepared;
    private readonly Action<string>? onCleared;

    public WorkspaceDocumentGenerator(
        Action<WorkspaceDocument>? onPrepared = null,
        Action<string>? onCleared = null)
    {
        this.onPrepared = onPrepared;
        this.onCleared = onCleared;
    }

    public override IDockable? CreateDocumentContainer(IItemsSourceDock dock, object item, int index)
    {
        return item is WorkspaceTabViewModel ? new WorkspaceDocument() : null;
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
