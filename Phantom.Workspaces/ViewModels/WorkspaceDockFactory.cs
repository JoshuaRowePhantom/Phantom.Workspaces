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
            Id = "MainDocumentDock",
            Title = "Documents",
            CanCreateDocument = false,
            VisibleDockables = CreateList<IDockable>(),
        };

        var root = CreateRootDock();
        root.Id = "Root";
        root.Title = "Root";
        root.VisibleDockables = CreateList<IDockable>(documentDock);
        root.DefaultDockable = documentDock;
        root.ActiveDockable = documentDock;

        return root;
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
