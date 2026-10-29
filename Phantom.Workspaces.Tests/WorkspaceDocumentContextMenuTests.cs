using Avalonia.Automation;
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

    [AvaloniaFact]
    public async Task DocumentContextMenu_ContainerReused_DropsOldTargetAndSubscriptions()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var provider = new ConfigurableProvider(Contributed("contributed", "Contributed command"));
        var context = CreateMenuContext(vm, "doc-reuse", provider, resetPaneTabs: true);
        var factory = new WorkspaceDocumentMenuFactory();
        var tabItem = new DocumentTabStripItem
        {
            DataContext = context.Document,
            DocumentContextMenu = CreateDefaultMenu(),
        };
        var menu = (ContextMenu)tabItem.DocumentContextMenu!;

        factory.Apply(menu, tabItem);
        Assert.Equal(1, provider.SubscriberCount);
        Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Contributed command"));

        factory.Clear(menu);

        Assert.Equal(0, provider.SubscriberCount);
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Contributed command"));
        Assert.Empty(menu.Items.OfType<Separator>());
    }

    [AvaloniaFact]
    public async Task DocumentContextMenu_TwoOpenInstances_KeepIndependentTargets()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var providerA = new RecordingProvider();
        var providerB = new RecordingProvider();
        var contextA = CreateMenuContext(vm, "doc-a", providerA, resetPaneTabs: true);
        var contextB = CreateMenuContext(vm, "doc-b", providerB, resetPaneTabs: false);
        var factory = new WorkspaceDocumentMenuFactory();

        var tabItemA = new DocumentTabStripItem { DataContext = contextA.Document, DocumentContextMenu = CreateDefaultMenu() };
        var tabItemB = new DocumentTabStripItem { DataContext = contextB.Document, DocumentContextMenu = CreateDefaultMenu() };
        var menuA = (ContextMenu)tabItemA.DocumentContextMenu!;
        var menuB = (ContextMenu)tabItemB.DocumentContextMenu!;

        factory.Apply(menuA, tabItemA);
        factory.Apply(menuB, tabItemB);

        var itemA = Assert.Single(menuA.Items.OfType<MenuItem>(), item => Equals(item.Header, "Contributed command"));
        var itemB = Assert.Single(menuB.Items.OfType<MenuItem>(), item => Equals(item.Header, "Contributed command"));
        Assert.NotSame(itemA, itemB);

        itemA.Command!.Execute(null);
        itemB.Command!.Execute(null);
        var invokedA = await providerA.LastExecution.Task;
        var invokedB = await providerB.LastExecution.Task;

        Assert.Same(contextA.Tab, invokedA.Tab);
        Assert.Same(contextB.Tab, invokedB.Tab);
    }

    [AvaloniaFact]
    public async Task DocumentContextMenu_InvisibleGroups_NormalizesContributionSeparators()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var provider = new ConfigurableProvider(
            Contributed("v1", "Visible one", group: "g1"),
            Contributed("h2", "Hidden two", group: "g2", visible: false),
            Contributed("v3", "Visible three", group: "g3"));
        var context = CreateMenuContext(vm, "doc-separators", provider, resetPaneTabs: true);
        var factory = new WorkspaceDocumentMenuFactory();
        var tabItem = new DocumentTabStripItem { DataContext = context.Document, DocumentContextMenu = CreateDefaultMenu() };
        var menu = (ContextMenu)tabItem.DocumentContextMenu!;

        factory.Apply(menu, tabItem);

        var contributed = menu.Items.OfType<MenuItem>()
            .Where(item => Equals(item.Header, "Visible one") || Equals(item.Header, "Visible three"))
            .ToList();
        Assert.Equal(2, contributed.Count);
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Hidden two"));

        var items = menu.Items.Cast<object>().ToList();
        Assert.IsNotType<Separator>(items[^1]);
        for (var index = 1; index < items.Count; index++)
        {
            Assert.False(items[index] is Separator && items[index - 1] is Separator, "No doubled separators allowed.");
        }

        // One boundary separator (visible defaults present) plus one between the two visible groups.
        Assert.Equal(2, menu.Items.OfType<Separator>().Count());

        var hiddenOnly = new ConfigurableProvider(Contributed("hidden", "Hidden only", visible: false));
        var hiddenContext = CreateMenuContext(vm, "doc-hidden-only", hiddenOnly, resetPaneTabs: false);
        var hiddenTabItem = new DocumentTabStripItem { DataContext = hiddenContext.Document, DocumentContextMenu = CreateDefaultMenu() };
        var hiddenMenu = (ContextMenu)hiddenTabItem.DocumentContextMenu!;
        factory.Apply(hiddenMenu, hiddenTabItem);
        Assert.Empty(hiddenMenu.Items.OfType<Separator>());
        Assert.Equal(2, hiddenMenu.Items.OfType<MenuItem>().Count());
    }

    [AvaloniaFact]
    public async Task DocumentContextMenu_ProviderInvalidates_UpdatesEnabledReasonWhileOpen()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var provider = new MutableReasonProvider("Reason X");
        var context = CreateMenuContext(vm, "doc-invalidate", provider, resetPaneTabs: true);
        var factory = new WorkspaceDocumentMenuFactory();
        var tabItem = new DocumentTabStripItem { DataContext = context.Document, DocumentContextMenu = CreateDefaultMenu() };
        var menu = (ContextMenu)tabItem.DocumentContextMenu!;

        factory.Apply(menu, tabItem);
        var before = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Mutable"));
        Assert.False(before.IsEnabled);
        Assert.Equal("Reason X", ToolTip.GetTip(before));

        provider.SetReason("Reason Y");
        factory.Apply(menu, tabItem);

        var after = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Mutable"));
        Assert.Equal("Reason Y", ToolTip.GetTip(after));
    }

    [AvaloniaFact]
    public async Task DocumentContextMenu_DefaultCapabilitiesChange_PreservesPinnedThemeBindings()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var provider = new ConfigurableProvider(Contributed("contributed", "Contributed command"));
        var context = CreateMenuContext(vm, "doc-defaults", provider, resetPaneTabs: true);
        var factory = new WorkspaceDocumentMenuFactory();
        var menu = CreateStockMenu(out var defaultHeaders);
        var tabItem = new DocumentTabStripItem { DataContext = context.Document, DocumentContextMenu = menu };

        factory.Apply(menu, tabItem);

        foreach (var header in defaultHeaders)
        {
            var defaultItem = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, header));
            Assert.NotNull(defaultItem.Command);
        }

        factory.Clear(menu);

        Assert.Equal(defaultHeaders.Count, menu.Items.OfType<MenuItem>().Count());
        Assert.Empty(menu.Items.OfType<Separator>());
    }

    [AvaloniaFact]
    public async Task DocumentContextMenu_TabOrientationChanges_PreservesDirectionalCloseBindings()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var provider = new ConfigurableProvider(Contributed("contributed", "Contributed command"));
        var context = CreateMenuContext(vm, "doc-orientation", provider, resetPaneTabs: true);
        var factory = new WorkspaceDocumentMenuFactory();
        var menu = new ContextMenu();
        var directional = new[] { "Close tabs to the left", "Close tabs to the right", "Close tabs above", "Close tabs below" };
        foreach (var header in directional)
        {
            menu.Items.Add(new MenuItem { Header = header, Command = new AsyncRelayCommand(_ => Task.CompletedTask) });
        }
        var tabItem = new DocumentTabStripItem { DataContext = context.Document, DocumentContextMenu = menu };

        factory.Apply(menu, tabItem);
        factory.Clear(menu);

        foreach (var header in directional)
        {
            var item = Assert.Single(menu.Items.OfType<MenuItem>(), candidate => Equals(candidate.Header, header));
            Assert.NotNull(item.Command);
        }
    }

    [AvaloniaFact]
    public async Task DocumentContextMenu_DisabledContribution_ExposesAccessibleLabelAndReason()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var descriptor = new TabCommandDescriptor
        {
            Id = "disabled",
            Header = "Disabled command",
            AccessibleLabel = "Disabled command accessible",
            Availability = _ => TabCommandAvailability.Disabled("Not allowed right now."),
            ExecuteAsync = (_, _) => Task.FromResult(TabCommandResult.Success()),
        };
        var provider = new ConfigurableProvider(descriptor);
        var context = CreateMenuContext(vm, "doc-disabled", provider, resetPaneTabs: true);
        var factory = new WorkspaceDocumentMenuFactory();
        var tabItem = new DocumentTabStripItem { DataContext = context.Document, DocumentContextMenu = CreateDefaultMenu() };
        var menu = (ContextMenu)tabItem.DocumentContextMenu!;

        factory.Apply(menu, tabItem);

        var item = Assert.Single(menu.Items.OfType<MenuItem>(), candidate => Equals(candidate.Header, "Disabled command"));
        Assert.False(item.IsEnabled);
        Assert.Equal("Disabled command accessible", AutomationProperties.GetName(item));
        Assert.Equal("Not allowed right now.", ToolTip.GetTip(item));
    }

    [AvaloniaFact]
    public async Task DocumentContextMenu_MenuClosesAfterInvoke_DoesNotCancelCommittedAction()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var descriptor = new TabCommandDescriptor
        {
            Id = "committed",
            Header = "Committed command",
            ExecuteAsync = async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
                completed.TrySetResult();
                return TabCommandResult.Success();
            },
        };
        var provider = new ConfigurableProvider(descriptor);
        var context = CreateMenuContext(vm, "doc-committed", provider, resetPaneTabs: true);
        var factory = new WorkspaceDocumentMenuFactory();
        var tabItem = new DocumentTabStripItem { DataContext = context.Document, DocumentContextMenu = CreateDefaultMenu() };
        var menu = (ContextMenu)tabItem.DocumentContextMenu!;

        factory.Apply(menu, tabItem);
        var item = Assert.Single(menu.Items.OfType<MenuItem>(), candidate => Equals(candidate.Header, "Committed command"));
        item.Command!.Execute(null);
        await started.Task;

        factory.Clear(menu);
        release.TrySetResult();

        await completed.Task;
    }

    private static TabCommandDescriptor Contributed(string id, string header, string group = "default", bool visible = true)
        => new()
        {
            Id = id,
            Header = header,
            Group = group,
            Availability = _ => visible ? TabCommandAvailability.Enabled : TabCommandAvailability.Hidden,
            ExecuteAsync = (_, _) => Task.FromResult(TabCommandResult.Success()),
        };

    private static ContextMenu CreateStockMenu(out IReadOnlyList<string> headers)
    {
        var stock = new[] { "Float", "Float all", "Close", "Close other tabs", "Close all tabs" };
        var menu = new ContextMenu();
        foreach (var header in stock)
        {
            menu.Items.Add(new MenuItem { Header = header, Command = new AsyncRelayCommand(_ => Task.CompletedTask) });
        }

        headers = stock;
        return menu;
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

    private sealed class ConfigurableProvider(params TabCommandDescriptor[] descriptors) : ITabCommandProvider
    {
        private EventHandler? commandsChanged;

        public int SubscriberCount { get; private set; }

        public IReadOnlyList<TabCommandDescriptor> GetCommands(TabCommandContext context) => descriptors;

        public event EventHandler? CommandsChanged
        {
            add
            {
                this.SubscriberCount++;
                this.commandsChanged += value;
            }
            remove
            {
                this.SubscriberCount--;
                this.commandsChanged -= value;
            }
        }

        public void Raise() => this.commandsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class MutableReasonProvider(string initialReason) : ITabCommandProvider
    {
        private string reason = initialReason;

        public IReadOnlyList<TabCommandDescriptor> GetCommands(TabCommandContext context)
            =>
            [
                new TabCommandDescriptor
                {
                    Id = "mutable",
                    Header = "Mutable",
                    Availability = _ => TabCommandAvailability.Disabled(this.reason),
                    ExecuteAsync = (_, _) => Task.FromResult(TabCommandResult.Success()),
                },
            ];

        public event EventHandler? CommandsChanged;

        public void SetReason(string value)
        {
            this.reason = value;
            this.CommandsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
