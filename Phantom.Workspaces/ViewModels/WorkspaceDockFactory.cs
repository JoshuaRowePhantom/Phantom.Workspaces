using System;
using System.Collections.Generic;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Phantom.Workspaces.Controls;

namespace Phantom.Workspaces.ViewModels;

public class WorkspaceDockFactory : Factory
{
    private readonly MainWindowViewModel mainWindowViewModel;

    public WorkspaceDockFactory(MainWindowViewModel mainWindowViewModel)
    {
        this.mainWindowViewModel = mainWindowViewModel;
    }

    /// <summary>
    /// Creates the root layout with a DocumentDock for workspace-level tabs.
    /// Each workspace tab contains its own nested ContentLayout for workspace content.
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
    /// Creates a dock layout for workspace content (entity tabs, agent sessions, etc.)
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

    public WorkspacePaneDocument CreateWorkspacePaneDocument(WorkspacePaneViewModel workspacePane)
    {
        return new WorkspacePaneDocument(workspacePane)
        {
            Id = workspacePane.Id,
            Title = workspacePane.Title,
            CanClose = true,
            CanFloat = false,
            CanPin = false,
        };
    }

    public void AddWorkspacePane(IDocumentDock dock, WorkspacePaneViewModel workspacePane)
    {
        var document = CreateWorkspacePaneDocument(workspacePane);
        AddDockable(dock, document);
        SetActiveDockable(document);
        SetFocusedDockable(dock, document);
    }

    public WorkspaceDocument CreateWorkspaceTabDocument(WorkspaceTabViewModel tabViewModel)
    {
        return new WorkspaceDocument(tabViewModel)
        {
            Id = tabViewModel.Id,
            Title = tabViewModel.Title,
            CanClose = true,
        };
    }

    public void AddWorkspaceTab(IDocumentDock dock, WorkspaceTabViewModel tabViewModel, bool focus = true)
    {
        var document = CreateWorkspaceTabDocument(tabViewModel);
        AddDockable(dock, document);
        if (focus)
        {
            SetActiveDockable(document);
            SetFocusedDockable(dock, document);
        }
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["Root"] = () => mainWindowViewModel,
        };

        DockableLocator = new Dictionary<string, Func<IDockable?>>
        {
        };

        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new PhantomHostWindow(),
        };

        base.InitLayout(layout);
    }
}
