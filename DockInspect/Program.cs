using System;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

var factory = new Factory();

// === Simulate CreateWorkspaceContentLayout for pane B ===
var contentDockB = new DocumentDock
{
    Id = "WorkspaceContent_B",
    CanCreateDocument = false,
    IsCollapsable = false,
    VisibleDockables = factory.CreateList<IDockable>(),
};

var rootB = factory.CreateRootDock();
rootB.Id = "WorkspaceContentRoot_B";
rootB.IsCollapsable = false;
rootB.VisibleDockables = factory.CreateList<IDockable>(contentDockB);
rootB.DefaultDockable = contentDockB;
rootB.ActiveDockable = contentDockB;
factory.InitLayout(rootB);

// === Simulate OpenTabAsync(tabB) ===
var tabBDoc = new Document { Id = "notif-pane-switch-tab-b", Title = "Tab B" };
factory.AddDockable(contentDockB, tabBDoc);
factory.SetActiveDockable(tabBDoc);
factory.SetFocusedDockable(contentDockB, tabBDoc);

Console.WriteLine($"After OpenTabAsync: contentDockB.ActiveDockable = {contentDockB.ActiveDockable?.Id ?? "null"}");

// === Simulate GoToWorkspacePaneAtIndexCommand.Execute("0") (switch to pane A) ===
// Just simulate finding pane A's content dock (empty) and checking it
var contentDockA = new DocumentDock
{
    Id = "WorkspaceContent_A",
    CanCreateDocument = false,
    VisibleDockables = factory.CreateList<IDockable>(),
};
var rootA = factory.CreateRootDock();
rootA.Id = "WorkspaceContentRoot_A";
rootA.VisibleDockables = factory.CreateList<IDockable>(contentDockA);
rootA.DefaultDockable = contentDockA;
rootA.ActiveDockable = contentDockA;
factory.InitLayout(rootA);

// In OnGoToWorkspacePaneAtIndex, we just read, don't write
Console.WriteLine($"pane A: contentDockA.ActiveDockable = {contentDockA.ActiveDockable?.Id ?? "null"}");

// === Simulate GoToWorkspacePaneAtIndexCommand.Execute("1") (switch back to pane B) ===
// FindDocumentDock(rootB)
IDockable? FindDocumentDock(IDockable dockable) {
    if (dockable is IDocumentDock dd) return dd;
    if (dockable is IDock dock && dock.VisibleDockables is not null)
        foreach (var child in dock.VisibleDockables) {
            var r = FindDocumentDock(child);
            if (r != null) return r;
        }
    return null;
}

var foundDock = FindDocumentDock(rootB) as IDocumentDock;
Console.WriteLine($"FindDocumentDock(rootB) = {foundDock?.Id ?? "null"}");
Console.WriteLine($"foundDock.ActiveDockable = {foundDock?.ActiveDockable?.Id ?? "null"}");
Console.WriteLine($"foundDock.ActiveDockable is Document: {foundDock?.ActiveDockable is Document}");
