using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services.TabCommands;
using Phantom.Workspaces.ViewModels;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspaceDocumentContextMenuTests
{
    [AvaloniaFact]
    public async Task DocumentContextMenu_RepeatedOpen_DoesNotDuplicateCommands()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var provider = new RecordingProvider();
        var context = CreateMenuContext(vm, "doc-a", provider, resetPaneTabs: true);
        var factory = new WorkspaceDocumentMenuFactory();
        var tabItem = new DocumentTabStripItem
        {
            DataContext = context.Document,
            DocumentContextMenu = CreateDefaultMenu(),
        };
        var menu = (ContextMenu)tabItem.DocumentContextMenu!;

        factory.Apply(menu, tabItem);
        factory.Clear(menu);
        factory.Apply(menu, tabItem);

        Assert.Equal(1, menu.Items.OfType<MenuItem>().Count(item => Equals(item.Header, "Contributed command")));
    }

    [AvaloniaFact]
    public async Task DocumentContextMenu_InactiveTab_InvokesClickedDocument()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var providerA = new RecordingProvider();
        var providerB = new RecordingProvider();
        var contextA = CreateMenuContext(vm, "doc-a", providerA, resetPaneTabs: true);
        var contextB = CreateMenuContext(vm, "doc-b", providerB, resetPaneTabs: false);
        contextA.Pane.Tabs.Add(contextB.Tab);
        contextA.Pane.RegisterDocument(contextB.Tab.Id, contextB.Document);
        contextB.Document.Owner = contextA.DocumentDock;
        contextA.DocumentDock.VisibleDockables!.Add(contextB.Document);
        contextA.Pane.SelectedTab = contextB.Tab;

        var factory = new WorkspaceDocumentMenuFactory();
        var tabItem = new DocumentTabStripItem
        {
            DataContext = contextA.Document,
            DocumentContextMenu = CreateDefaultMenu(),
        };
        var menu = (ContextMenu)tabItem.DocumentContextMenu!;
        factory.Apply(menu, tabItem);
        var contributed = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Contributed command"));
        Assert.NotNull(contributed.Command);

        contributed.Command!.Execute(null);
        var invokedContext = await providerA.LastExecution.Task;

        Assert.Same(contextA.Tab, invokedContext.Tab);
        Assert.NotSame(contextB.Tab, invokedContext.Tab);
    }

    private static ContextMenu CreateDefaultMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Close" });
        menu.Items.Add(new MenuItem { Header = "Close other tabs" });
        return menu;
    }

    private static (WorkspacePaneViewModel Pane, WorkspaceTabViewModel Tab, WorkspaceDocument Document, IDock DocumentDock) CreateMenuContext(
        MainWindowViewModel vm,
        string tabId,
        ITabCommandProvider provider,
        bool resetPaneTabs)
    {
        var pane = vm.WorkspacePanes.First();
        if (resetPaneTabs)
        {
            pane.Tabs.Clear();
        }
        var tab = new ProviderTab(provider)
        {
            Id = tabId,
            Title = tabId,
            Entity = CreateWorkspaceEntity(tabId),
        };
        pane.Tabs.Add(tab);

        var document = new WorkspaceDocument(tab);
        var dock = new WorkspaceContentDock
        {
            Id = $"dock-{tabId}",
            Context = pane,
            VisibleDockables = new ObservableCollection<IDockable> { document },
        };
        var root = new RootDock
        {
            Id = "root",
            Context = vm,
            VisibleDockables = new ObservableCollection<IDockable> { dock },
        };
        dock.Owner = root;
        document.Owner = dock;
        pane.RegisterDocument(tab.Id, document);
        pane.SelectedTab = tab;
        vm.SelectedWorkspacePane = pane;

        return (pane, tab, document, dock);
    }

    private static SubscribedEntityViewModel CreateWorkspaceEntity(string id)
        => new(new EntitySnapshot
        {
            EntityId = new EntityId(Guid.NewGuid().ToString("N")),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = JsonDocument.Parse($$"""
                {
                  "entity-id": "{{id}}",
                  "display-name": { "default": "{{id}}" },
                  "entity-types": ["entity", "workspace"]
                }
                """).RootElement.Clone(),
            Relationships = [],
        });

    private sealed class ProviderTab(ITabCommandProvider provider) : WorkspaceTabViewModel
    {
        public override ITabCommandProvider TabCommands { get; } = provider;
    }

    private sealed class RecordingProvider : ITabCommandProvider
    {
        public TaskCompletionSource<TabCommandContext> LastExecution { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<TabCommandDescriptor> GetCommands(TabCommandContext context)
            =>
            [
                new TabCommandDescriptor
                {
                    Id = "contributed",
                    Header = "Contributed command",
                    ExecuteAsync = (ctx, _) =>
                    {
                        this.LastExecution.TrySetResult(ctx);
                        return Task.FromResult(TabCommandResult.Success());
                    },
                },
            ];

        public event EventHandler? CommandsChanged
        {
            add { }
            remove { }
        }
    }
}
