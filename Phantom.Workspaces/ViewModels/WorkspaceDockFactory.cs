using System;
using System.Collections.Generic;
using System.Linq;
using Dock.Avalonia.Controls;
using Dock.Model;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Phantom.Workspaces.Controls;

namespace Phantom.Workspaces.ViewModels;

public class WorkspaceDockFactory : Factory
{
    private readonly MainWindowViewModel mainWindowViewModel;

    private readonly DockState dockState = new();

    /// <summary>
    /// Captures and restores <see cref="Dock.Model.Core.IDocumentContent.Content"/> state
    /// so that open document content is preserved across dock-layout serialization round-trips.
    /// </summary>
    public DockState DockState => dockState;

    /// <summary>
    /// Registry mapping tab IDs to their dock documents. Populated by
    /// <see cref="WorkspaceDocumentGenerator"/> via the onPrepared callback.
    /// </summary>
    private readonly Dictionary<string, WorkspaceDocument> documentsByTabId = new(StringComparer.Ordinal);

    /// <summary>
    /// Registry mapping pane IDs to their dock documents. Populated by
    /// <see cref="WorkspacePaneDocumentGenerator"/> via the onPrepared callback.
    /// </summary>
    private readonly Dictionary<string, WorkspacePaneDocument> paneDocumentsByPaneId = new(StringComparer.Ordinal);

    public WorkspaceDockFactory(MainWindowViewModel mainWindowViewModel)
    {
        this.mainWindowViewModel = mainWindowViewModel;
    }

    /// <summary>
    /// Returns the <see cref="WorkspaceDocument"/> registered for the given tab ID, or null if none.
    /// </summary>
    public WorkspaceDocument? GetDocumentForTab(string tabId)
        => this.documentsByTabId.TryGetValue(tabId, out var doc) ? doc : null;

    /// <summary>
    /// Removes the document registration for the given tab ID (called when a document is cleared).
    /// </summary>
    public void UnregisterDocument(string tabId)
    {
        this.documentsByTabId.Remove(tabId);
        this.DockableLocator?.Remove(tabId);
    }

    /// <summary>
    /// Registers a document for the given tab ID (called when restoring from dock-layout JSON).
    /// Also updates <see cref="IDockFactory.DockableLocator"/> so the Dock library can locate
    /// the document by ID when re-wiring the layout.
    /// </summary>
    public void RegisterDocument(string tabId, WorkspaceDocument document)
    {
        this.documentsByTabId[tabId] = document;
        if (this.DockableLocator is not null)
            this.DockableLocator[tabId] = () => this.documentsByTabId.GetValueOrDefault(tabId);
    }

    /// <summary>
    /// #1334: uniformly wires a <see cref="WorkspaceContentDock"/> region so that fresh, restored,
    /// primary, secondary, and menu-split docks are configured identically. The single owns-tabs
    /// "primary" dock is bound to the pane's <see cref="WorkspacePaneViewModel.Tabs"/> via
    /// <c>ItemsSource</c> and a live <see cref="WorkspaceDocumentGenerator"/>; every other region
    /// re-initializes and registers its already-materialized restored documents so
    /// <see cref="GetDocumentForTab"/> and the Ctrl-click / <c>NewWindowRequested</c> anchor
    /// resolution route each tab to the region that actually hosts it (#1333). Centralizing the
    /// wiring here removes the DFS-first-only asymmetry that left non-primary restored regions
    /// mis-registered.
    /// </summary>
    public void WireContentDock(WorkspaceContentDock dock, WorkspacePaneViewModel workspacePane, bool ownsTabs)
    {
        if (ownsTabs)
        {
            dock.VisibleDockables?.Clear();
            dock.ItemsSource = workspacePane.Tabs;
            dock.ItemContainerGenerator = new WorkspaceDocumentGenerator(
                this,
                doc => this.RegisterDocument(doc.Id, doc),
                id => this.UnregisterDocument(id));
        }
        else if (dock.VisibleDockables is { } stubs)
        {
            foreach (var stub in stubs.OfType<WorkspaceDocument>())
            {
                // The stub's Context was pre-wired by InitLayout's ContextLocator but not fully
                // initialized (no header, no tab-event subscriptions). Complete initialization and
                // register it so the region's tabs render their header template and resolve to the
                // owning dock for Ctrl-click / NewWindowRequested (#1333).
                if (stub.TabViewModel is not { } tabVm)
                {
                    continue;
                }

                stub.Initialize(tabVm);
                if (!string.IsNullOrEmpty(stub.Id))
                {
                    this.RegisterDocument(stub.Id, stub);
                }
            }
        }
    }

    /// <summary>
    /// Returns the <see cref="WorkspacePaneDocument"/> registered for the given pane ID, or null if none.
    /// </summary>
    public WorkspacePaneDocument? GetPaneDocument(string paneId)
        => this.paneDocumentsByPaneId.TryGetValue(paneId, out var doc) ? doc : null;

    /// <summary>
    /// Creates the root layout with a DocumentDock for workspace-level tabs.
    /// Uses ItemsSource wired to <see cref="MainWindowViewModel.WorkspacePanes"/> so that
    /// adding/removing workspace panes automatically creates/destroys dock documents.
    /// </summary>
    public override IRootDock CreateLayout()
    {
        var workspacesDock = new WorkspacesPaneDock
        {
            Id = "WorkspacesDock",
            Title = "Workspaces",
            CanCreateDocument = false,
            IsCollapsable = false,
            CanFloat = false,
            CanPin = false,
            VisibleDockables = CreateList<IDockable>(),
            ItemsSource = mainWindowViewModel.WorkspacePanes,
            ItemContainerGenerator = new WorkspacePaneDocumentGenerator(
                doc => this.paneDocumentsByPaneId[doc.Id] = doc,
                id => this.paneDocumentsByPaneId.Remove(id)),
        };

        var root = CreateRootDock();
        root.Id = "Root";
        root.Title = "Root";
        root.IsCollapsable = false;
        root.CanFloat = false;
        root.CanPin = false;
        root.VisibleDockables = CreateList<IDockable>(workspacesDock);
        root.DefaultDockable = workspacesDock;
        root.ActiveDockable = workspacesDock;

        return root;
    }

    /// <summary>
    /// #1307: split-created docks (via Dock's ``NewHorizontalDocumentDock`` /
    /// ``NewVerticalDocumentDock`` paths) must be ``WorkspaceContentDock`` so tabs
    /// they host match the rich header template in ``DockDataTemplates.axaml``
    /// (favicon + ``MaxWidth=180`` + ``CharacterEllipsis``). A plain ``DocumentDock``
    /// would fall through to the generic ``IDocumentDock`` template, which has no
    /// ``HeaderTemplate`` and would render an unbounded, icon-less tab title.
    /// A fresh ``Id`` is assigned to prevent ``FactoryBase`` from copying the source
    /// dock's ``Id`` on split (which would create duplicate ids in the layout).
    /// </summary>
    public override IDocumentDock CreateDocumentDock()
    {
        return new WorkspaceContentDock
        {
            Id = Guid.NewGuid().ToString(),
        };
    }

    /// <summary>
    /// Creates a dock layout for workspace content (entity tabs, agent sessions, etc.)
    /// Uses ItemsSource wired to <see cref="WorkspacePaneViewModel.Tabs"/> so that
    /// adding/removing tabs automatically creates/destroys dock documents via
    /// <see cref="WorkspaceDocumentGenerator"/>.
    /// </summary>
    public IRootDock CreateWorkspaceContentLayout(WorkspacePaneViewModel workspacePane)
    {
        var contentDock = new WorkspaceContentDock
        {
            Id = $"WorkspaceContent_{workspacePane.Id}",
            Title = workspacePane.Title,
            CanCreateDocument = false,
            IsCollapsable = false,
            VisibleDockables = CreateList<IDockable>(),
            ItemsSource = workspacePane.Tabs,
            ItemContainerGenerator = new WorkspaceDocumentGenerator(
                this,
                doc => this.documentsByTabId[doc.Id] = doc,
                id => this.documentsByTabId.Remove(id)),
        };

        var root = CreateRootDock();
        root.Id = $"WorkspaceContentRoot_{workspacePane.Id}";
        root.Title = workspacePane.Title;
        root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(contentDock);
        root.DefaultDockable = contentDock;
        root.ActiveDockable = contentDock;

        InitLayout(root);

        return root;
    }

    public override void OnDockableClosed(IDockable? dockable)
    {
        base.OnDockableClosed(dockable);
        if (dockable is WorkspaceDocument { TabViewModel: { } tabVm })
            mainWindowViewModel.OnDockableTabClosed(tabVm);
        else if (dockable is WorkspacePaneDocument paneDoc)
            mainWindowViewModel.OnWorkspacePaneDockableClosed(paneDoc);
    }

    public override void InitLayout(IDockable layout)
    {
        if (ContextLocator is null)
            ContextLocator = new Dictionary<string, Func<object?>>();
        ContextLocator["Root"] = () => mainWindowViewModel;

        DockableLocator ??= new Dictionary<string, Func<IDockable?>>();

        HostWindowLocator ??= new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () =>
            {
                // #1196: The main window's TopLevelDockControl is the first DockControl
                // that self-registers with this factory (MainWindow.axaml.cs populates
                // its DataTemplates with the shared instances). See
                // IFactory.DockControls (Dock.Model/Core/IFactory.cs) and DockControl
                // self-registration (Dock.Avalonia/Controls/DockControl.axaml.cs). Share
                // those same IDataTemplate instances with the floating host so no
                // duplicate `new DockDataTemplates()` collection is created per window.
                var sourceDockControl = this.DockControls.OfType<DockControl>().FirstOrDefault();
                return new PhantomHostWindow(sourceDockControl);
            },
        };

        base.InitLayout(layout);
    }
}
