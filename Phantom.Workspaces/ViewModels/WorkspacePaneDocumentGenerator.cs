using System;
using Dock.Model.Avalonia.Controls;
using Dock.Model.Core;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// ItemsSource container generator for <see cref="WorkspacesPaneDock"/>.
/// Creates a <see cref="WorkspacePaneDocument"/> for each <see cref="WorkspacePaneViewModel"/>
/// in the dock's ItemsSource.
/// </summary>
public sealed class WorkspacePaneDocumentGenerator : DockItemContainerGenerator
{
    private readonly Action<WorkspacePaneDocument>? onPrepared;
    private readonly Action<string>? onCleared;

    public WorkspacePaneDocumentGenerator(
        Action<WorkspacePaneDocument>? onPrepared = null,
        Action<string>? onCleared = null)
    {
        this.onPrepared = onPrepared;
        this.onCleared = onCleared;
    }

    public override IDockable? CreateDocumentContainer(IItemsSourceDock dock, object item, int index)
    {
        return item is WorkspacePaneViewModel pane ? new WorkspacePaneDocument(pane)
        {
            Id = pane.Id,
            Title = pane.Title,
            CanClose = true,
            CanFloat = false,
            CanPin = false,
        } : null;
    }

    public override void PrepareDocumentContainer(IItemsSourceDock dock, IDockable container, object item, int index)
    {
        if (container is WorkspacePaneDocument doc && item is WorkspacePaneViewModel pane)
        {
            // Set Context so FindGeneratedDocument can match item → document for removal.
            doc.Context = item;
            doc.Id = pane.Id;
            doc.Title = pane.Title;
            doc.CanClose = true;
            this.onPrepared?.Invoke(doc);
        }
    }

    public override void ClearDocumentContainer(IItemsSourceDock dock, IDockable container, object? item)
    {
        if (container is WorkspacePaneDocument doc)
        {
            this.onCleared?.Invoke(doc.Id);
        }
    }
}
