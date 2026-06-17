using System;
using System.Collections.Generic;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace Phantom.Workspaces.ViewModels;

public class WorkspaceDockFactory : Factory
{
    private readonly MainWindowViewModel mainWindowViewModel;

    public WorkspaceDockFactory(MainWindowViewModel mainWindowViewModel)
    {
        this.mainWindowViewModel = mainWindowViewModel;
    }

    public override IRootDock CreateLayout()
    {
        var documentDock = new DocumentDock
        {
            Id = "WorkspaceDocumentDock",
            Title = "Workspace",
            CanCreateDocument = false,
            IsCollapsable = false,
            VisibleDockables = CreateList<IDockable>(),
        };

        var root = CreateRootDock();
        root.Id = "Root";
        root.Title = "Root";
        root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(documentDock);
        root.DefaultDockable = documentDock;
        root.ActiveDockable = documentDock;

        return root;
    }

    public WorkspaceDocument CreateWorkspaceDocument(WorkspaceTabViewModel tabViewModel)
    {
        return new WorkspaceDocument(tabViewModel)
        {
            Id = tabViewModel.Id,
            Title = tabViewModel.Title,
            CanClose = true,
        };
    }

    public void AddDocument(IDocumentDock dock, WorkspaceTabViewModel tabViewModel)
    {
        var document = CreateWorkspaceDocument(tabViewModel);
        AddDockable(dock, document);
        SetActiveDockable(document);
        SetFocusedDockable(dock, document);
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
            [nameof(IDockWindow)] = () => new HostWindow(),
        };

        base.InitLayout(layout);
    }
}
