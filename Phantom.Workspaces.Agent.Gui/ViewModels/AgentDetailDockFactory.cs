using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>
/// Builds the locked, tab-strip-less <see cref="AgentDetailDocumentDock"/> that hosts the agent-chat
/// detail region (issue #1035). Every nav node's <c>DetailContent</c> — including each sub-agent
/// child — becomes a cached <see cref="AgentDetailDocument"/>; tree selection drives the active
/// document (cache-N/show-one). Mirrors <c>WorkspaceDockFactory</c>; deliberately never fed to
/// <c>Dock.Serializer</c> (the dock is ephemeral and fully derived from the editor tree).
/// </summary>
/// <remarks>
/// Documents are generated imperatively from the shared, flat detail-content collection (rather than
/// via the Avalonia model's <c>ItemsSource</c>) so the collection may mutate off the UI thread when a
/// sub-agent is added on a background scheduler without triggering a dispatcher cross-thread access
/// exception. The factory keeps the generated documents in lock-step with the collection.
/// </remarks>
public sealed class AgentDetailDockFactory : Factory
{
    private readonly Dictionary<AgentDetailDocumentItem, AgentDetailDocument> documentsByItem = new();
    private readonly IEnumerable itemsSource;
    private AgentDetailDocumentDock? detailDock;
    private IRootDock? layout;

    public AgentDetailDockFactory(IEnumerable itemsSource)
    {
        this.itemsSource = itemsSource;
        this.Build();
    }

    /// <summary>The root layout bound to the hosting <c>DockControl.Layout</c>.</summary>
    public IRootDock Layout => this.layout!;

    /// <summary>The locked detail dock (documents cached, one active/visible, no tab strip).</summary>
    public AgentDetailDocumentDock DetailDock => this.detailDock!;

    /// <summary>Returns the cached document generated for the given item, or null.</summary>
    public AgentDetailDocument? GetDocument(AgentDetailDocumentItem? item)
        => item is not null && this.documentsByItem.TryGetValue(item, out var doc) ? doc : null;

    /// <summary>The dock's currently active document, or null.</summary>
    public AgentDetailDocument? ActiveDocument => this.detailDock?.ActiveDockable as AgentDetailDocument;

    /// <summary>
    /// Activates the cached document for <paramref name="item"/>, driving the cache-N/show-one dock.
    /// No-op when the item has no generated document.
    /// </summary>
    public void SetActiveDetail(AgentDetailDocumentItem? item)
    {
        var doc = this.GetDocument(item);
        if (doc is null)
        {
            return;
        }

        foreach (var registered in this.documentsByItem.Keys)
        {
            registered.IsActive = ReferenceEquals(registered, item);
        }

        this.SetActiveDockable(doc);
    }

    private void Build()
    {
        var dock = new AgentDetailDocumentDock
        {
            Id = "AgentDetail",
            Title = "AgentDetail",
            CanCreateDocument = false,
            CanClose = false,
            CanFloat = false,
            CanDrag = false,
            CanDrop = false,
            CanPin = false,
            CanDockAsDocument = false,
            IsCollapsable = false,
            VisibleDockables = CreateList<IDockable>(),
        };

        var root = CreateRootDock();
        root.Id = "AgentDetailRoot";
        root.Title = "AgentDetailRoot";
        root.IsCollapsable = false;
        root.CanClose = false;
        root.CanFloat = false;
        root.CanPin = false;
        root.VisibleDockables = CreateList<IDockable>(dock);
        root.DefaultDockable = dock;
        root.ActiveDockable = dock;

        this.detailDock = dock;
        this.layout = root;

        InitLayout(root);

        // Generate a cached document for each existing item, then track the collection so every nav
        // node — including sub-agents added later — always has a first-class cached document.
        foreach (var item in this.itemsSource)
        {
            if (item is AgentDetailDocumentItem detailItem)
            {
                this.AddDocumentForItem(detailItem);
            }
        }

        if (this.itemsSource is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged += this.OnItemsChanged;
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var item in new List<AgentDetailDocumentItem>(this.documentsByItem.Keys))
            {
                this.RemoveDocumentForItem(item);
            }

            foreach (var item in this.itemsSource)
            {
                if (item is AgentDetailDocumentItem detailItem)
                {
                    this.AddDocumentForItem(detailItem);
                }
            }

            return;
        }

        if (e.NewItems is not null)
        {
            foreach (AgentDetailDocumentItem item in e.NewItems)
            {
                this.AddDocumentForItem(item);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (AgentDetailDocumentItem item in e.OldItems)
            {
                this.RemoveDocumentForItem(item);
            }
        }
    }

    private void AddDocumentForItem(AgentDetailDocumentItem item)
    {
        if (this.documentsByItem.ContainsKey(item) || this.detailDock is null)
        {
            return;
        }

        var doc = new AgentDetailDocument();
        doc.Initialize(item);
        this.documentsByItem[item] = doc;
        this.AddDockable(this.detailDock, doc);

        // The first document generated becomes the active/visible one; subsequent additions must not
        // steal the active document from the user's current selection (handled by the dock override,
        // but AddDockable does not activate, so make the initial document active explicitly).
        this.detailDock.ActiveDockable ??= doc;
    }

    private void RemoveDocumentForItem(AgentDetailDocumentItem item)
    {
        if (!this.documentsByItem.TryGetValue(item, out var doc) || this.detailDock is null)
        {
            return;
        }

        this.documentsByItem.Remove(item);
        this.detailDock.VisibleDockables?.Remove(doc);
        if (ReferenceEquals(this.detailDock.ActiveDockable, doc))
        {
            this.detailDock.ActiveDockable = this.detailDock.VisibleDockables is { Count: > 0 } list
                ? list[0]
                : null;
        }
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator ??= new Dictionary<string, Func<object?>>();
        DockableLocator ??= new Dictionary<string, Func<IDockable?>>();
        HostWindowLocator ??= new Dictionary<string, Func<IHostWindow?>>();
        base.InitLayout(layout);
    }
}
