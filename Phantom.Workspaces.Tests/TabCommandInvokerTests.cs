using Dock.Model.Controls;
using Dock.Model.Core;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services.TabCommands;
using Phantom.Workspaces.ViewModels;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace Phantom.Workspaces.Tests;

public sealed class TabCommandInvokerTests
{
    [Fact]
    public async Task TabCommands_DefaultProvider_ReturnsNoCommands()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity("pane-default"));
        var tab = new PlainTab
        {
            Id = "plain-tab",
            Title = "Plain",
            Entity = CreateWorkspaceEntity("plain-tab"),
        };

        var context = CreateContext(vm, pane, tab);
        Assert.Empty(tab.TabCommands.GetCommands(context));
    }

    [Fact]
    public async Task TabCommands_DifferentTabSubclasses_UseTheirOwnProviders()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity("pane-providers"));
        var providerA = new FixedProvider("command-a");
        var providerB = new FixedProvider("command-b");
        var tabA = new ProviderTab(providerA)
        {
            Id = "tab-a",
            Title = "A",
            Entity = CreateWorkspaceEntity("tab-a"),
        };
        var tabB = new ProviderTab(providerB)
        {
            Id = "tab-b",
            Title = "B",
            Entity = CreateWorkspaceEntity("tab-b"),
        };

        var contextA = CreateContext(vm, pane, tabA);
        var contextB = CreateContext(vm, pane, tabB);

        Assert.Equal("command-a", Assert.Single(tabA.TabCommands.GetCommands(contextA)).Id);
        Assert.Equal("command-b", Assert.Single(tabB.TabCommands.GetCommands(contextB)).Id);
    }

    [Fact]
    public async Task TabCommandInvoker_RepeatedSameTargetCommand_ExecutesOnce()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity("pane-single-flight"));
        var tab = new PlainTab
        {
            Id = "single-flight-tab",
            Title = "Single flight",
            Entity = CreateWorkspaceEntity("single-flight-tab"),
        };
        var context = CreateContext(vm, pane, tab);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executeCount = 0;
        var descriptor = new TabCommandDescriptor
        {
            Id = "single-flight",
            Header = "Single flight",
            ExecuteAsync = async (_, _) =>
            {
                Interlocked.Increment(ref executeCount);
                await gate.Task;
                return TabCommandResult.Success();
            },
        };

        var first = context.Invoker.InvokeAsync(descriptor, context, TestContext.Current.CancellationToken);
        var second = context.Invoker.InvokeAsync(descriptor, context, TestContext.Current.CancellationToken);
        gate.TrySetResult();
        var firstResult = await first;
        var secondResult = await second;

        Assert.Equal(1, executeCount);
        Assert.Equal(TabCommandResultKind.Succeeded, firstResult.Kind);
        Assert.Equal(TabCommandResultKind.AlreadyRunning, secondResult.Kind);
    }

    [Fact]
    public async Task TabCommandInvoker_TargetClosesOrLayoutReplaces_RejectsWithoutRetargeting()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity("pane-replaced"));
        var tab = new PlainTab
        {
            Id = "replace-tab",
            Title = "Replace",
            Entity = CreateWorkspaceEntity("replace-tab"),
        };
        var context = CreateContext(vm, pane, tab);
        var executeCount = 0;
        var descriptor = new TabCommandDescriptor
        {
            Id = "replace-check",
            Header = "Replace check",
            ExecuteAsync = (_, _) =>
            {
                executeCount++;
                return Task.FromResult(TabCommandResult.Success());
            },
        };

        var replacementDocument = new WorkspaceDocument(tab);
        replacementDocument.Owner = context.DocumentDock;
        pane.RegisterDocument(tab.Id, replacementDocument);
        var result = await context.Invoker.InvokeAsync(descriptor, context, TestContext.Current.CancellationToken);

        Assert.Equal(TabCommandResultKind.Rejected, result.Kind);
        Assert.Equal(0, executeCount);
    }

    private static TabCommandContext CreateContext(
        MainWindowViewModel mainWindow,
        WorkspacePaneViewModel pane,
        WorkspaceTabViewModel tab)
    {
        pane.Tabs.Add(tab);
        var document = new WorkspaceDocument(tab);
        var dock = new WorkspaceContentDock
        {
            Id = $"dock-{tab.Id}",
            VisibleDockables = new ObservableCollection<IDockable> { document },
        };
        document.Owner = dock;
        pane.RegisterDocument(tab.Id, document);
        mainWindow.WorkspacePanes.Clear();
        mainWindow.WorkspacePanes.Add(pane);
        mainWindow.SelectedWorkspacePane = pane;
        return new TabCommandContext(mainWindow, pane, dock, document, tab, mainWindow.TabCommandInvoker);
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

    private sealed class PlainTab : WorkspaceTabViewModel;

    private sealed class ProviderTab(ITabCommandProvider provider) : WorkspaceTabViewModel
    {
        public override ITabCommandProvider TabCommands { get; } = provider;
    }

    private sealed class FixedProvider(string commandId) : ITabCommandProvider
    {
        private readonly IReadOnlyList<TabCommandDescriptor> commands =
        [
            new TabCommandDescriptor
            {
                Id = commandId,
                Header = commandId,
                ExecuteAsync = (_, _) => Task.FromResult(TabCommandResult.Success()),
            },
        ];

        public IReadOnlyList<TabCommandDescriptor> GetCommands(TabCommandContext context) => this.commands;

        public event EventHandler? CommandsChanged
        {
            add { }
            remove { }
        }
    }
}
