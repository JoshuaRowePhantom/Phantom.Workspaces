using Dock.Model.Core;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Services.TabCommands;

public sealed class TabCommandContext
{
    public TabCommandContext(
        MainWindowViewModel? mainWindow,
        WorkspacePaneViewModel pane,
        IDock documentDock,
        WorkspaceDocument document,
        WorkspaceTabViewModel tab,
        TabCommandInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(pane);
        ArgumentNullException.ThrowIfNull(documentDock);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(invoker);

        this.MainWindow = mainWindow;
        this.Pane = pane;
        this.DocumentDock = documentDock;
        this.Document = document;
        this.Tab = tab;
        this.Invoker = invoker;
    }

    public MainWindowViewModel? MainWindow { get; }

    public WorkspacePaneViewModel Pane { get; }

    public IDock DocumentDock { get; }

    public WorkspaceDocument Document { get; }

    public WorkspaceTabViewModel Tab { get; }

    public TabCommandInvoker Invoker { get; }

    public bool IsStillValid()
    {
        if (!this.Pane.Tabs.Contains(this.Tab))
        {
            return false;
        }

        if (!ReferenceEquals(this.Pane.GetDocumentForTab(this.Tab.Id), this.Document))
        {
            return false;
        }

        if (!ReferenceEquals(this.Document.TabViewModel, this.Tab))
        {
            return false;
        }

        return this.Document.Owner is IDock owner
               && ReferenceEquals(owner, this.DocumentDock)
               && owner.VisibleDockables?.Contains(this.Document) == true;
    }

    public static bool TryCreateForExplicitTarget(
        MainWindowViewModel mainWindow,
        WorkspaceDocument document,
        out TabCommandContext? context)
    {
        ArgumentNullException.ThrowIfNull(mainWindow);
        ArgumentNullException.ThrowIfNull(document);

        context = null;
        if (document.Owner is not IDock documentDock)
        {
            return false;
        }

        if (document.TabViewModel is not { } tab)
        {
            return false;
        }

        var pane = mainWindow.WorkspacePanes.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.GetDocumentForTab(document.Id), document));
        if (pane is null)
        {
            return false;
        }

        context = new TabCommandContext(
            mainWindow,
            pane,
            documentDock,
            document,
            tab,
            mainWindow.TabCommandInvoker);
        return true;
    }
}
