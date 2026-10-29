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

    [Fact]
    public async Task TabCommandInvoker_SelectionChangesDuringAwait_KeepsCapturedTarget()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity("pane-captured"));
        var tab = new PlainTab
        {
            Id = "captured-tab",
            Title = "Captured",
            Entity = CreateWorkspaceEntity("captured-tab"),
        };
        var context = CreateContext(vm, pane, tab);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TabCommandContext? executedContext = null;
        var descriptor = new TabCommandDescriptor
        {
            Id = "captured",
            Header = "Captured",
            ExecuteAsync = async (ctx, _) =>
            {
                executedContext = ctx;
                await gate.Task;
                return TabCommandResult.Success();
            },
        };

        var invoke = context.Invoker.InvokeAsync(descriptor, context, TestContext.Current.CancellationToken);

        var otherPane = new WorkspacePaneViewModel(CreateWorkspaceEntity("pane-other"));
        vm.WorkspacePanes.Add(otherPane);
        vm.SelectedWorkspacePane = otherPane;
        gate.TrySetResult();
        var result = await invoke;

        Assert.Equal(TabCommandResultKind.Succeeded, result.Kind);
        Assert.Same(tab, executedContext!.Tab);
    }

    [Fact]
    public async Task TabCommandInvoker_CanExecuteFalse_DoesNotExecute()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity("pane-can-execute"));
        var tab = new PlainTab
        {
            Id = "can-execute-tab",
            Title = "CanExecute",
            Entity = CreateWorkspaceEntity("can-execute-tab"),
        };
        var context = CreateContext(vm, pane, tab);
        var executeCount = 0;
        var descriptor = new TabCommandDescriptor
        {
            Id = "can-execute",
            Header = "CanExecute",
            Availability = _ => TabCommandAvailability.Disabled("Disabled for this target."),
            ExecuteAsync = (_, _) =>
            {
                executeCount++;
                return Task.FromResult(TabCommandResult.Success());
            },
        };

        var result = await context.Invoker.InvokeAsync(descriptor, context, TestContext.Current.CancellationToken);

        Assert.Equal(TabCommandResultKind.Rejected, result.Kind);
        Assert.Equal("Disabled for this target.", result.Message);
        Assert.Equal(0, executeCount);
    }

    [Fact]
    public async Task TabCommandInvoker_ExecutionThrows_ReportsErrorAndClearsBusy()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity("pane-throws"));
        var tab = new PlainTab
        {
            Id = "throws-tab",
            Title = "Throws",
            Entity = CreateWorkspaceEntity("throws-tab"),
        };
        var sink = new RecordingErrorSink();
        var invoker = new TabCommandInvoker(sink);
        var context = CreateContextWithInvoker(vm, pane, tab, invoker);
        var failure = new InvalidOperationException("boom");
        var throwing = new TabCommandDescriptor
        {
            Id = "throws",
            Header = "Throws",
            ExecuteAsync = (_, _) => throw failure,
        };

        var result = await invoker.InvokeAsync(throwing, context, TestContext.Current.CancellationToken);

        Assert.Equal(TabCommandResultKind.Failed, result.Kind);
        Assert.Same(failure, sink.LastException);
        Assert.Same(tab, sink.LastContext!.Tab);
        Assert.Same(throwing, sink.LastCommand);

        var succeeding = new TabCommandDescriptor
        {
            Id = "throws",
            Header = "Throws",
            ExecuteAsync = (_, _) => Task.FromResult(TabCommandResult.Success()),
        };
        var retry = await invoker.InvokeAsync(succeeding, context, TestContext.Current.CancellationToken);
        Assert.Equal(TabCommandResultKind.Succeeded, retry.Kind);
    }

    [Fact]
    public async Task TabCommands_KeyboardOriginAndExplicitMenu_UseSharedOwnershipRules()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity("pane-ownership"));
        var tabA = new PlainTab { Id = "tab-a", Title = "A", Entity = CreateWorkspaceEntity("tab-a") };
        var tabB = new PlainTab { Id = "tab-b", Title = "B", Entity = CreateWorkspaceEntity("tab-b") };
        var documentA = new WorkspaceDocument(tabA);
        var documentB = new WorkspaceDocument(tabB);
        var dock = new WorkspaceContentDock
        {
            Id = "dock-ownership",
            VisibleDockables = new ObservableCollection<IDockable> { documentA, documentB },
        };
        documentA.Owner = dock;
        documentB.Owner = dock;
        pane.Tabs.Add(tabA);
        pane.Tabs.Add(tabB);
        pane.RegisterDocument(tabA.Id, documentA);
        pane.RegisterDocument(tabB.Id, documentB);
        pane.SelectedTab = tabB;
        vm.WorkspacePanes.Clear();
        vm.WorkspacePanes.Add(pane);
        vm.SelectedWorkspacePane = pane;

        Assert.True(TabCommandContext.TryCreateForKeyboardOrigin(vm, tabA, out var keyboardContext));
        Assert.Same(tabA, keyboardContext!.Tab);

        Assert.True(TabCommandContext.TryCreateForExplicitTarget(vm, documentA, out var explicitContext));
        Assert.Same(tabA, explicitContext!.Tab);
        Assert.NotSame(pane.SelectedTab, explicitContext.Tab);

        var orphan = new PlainTab { Id = "orphan", Title = "Orphan", Entity = CreateWorkspaceEntity("orphan") };
        Assert.False(TabCommandContext.TryCreateForKeyboardOrigin(vm, orphan, out var orphanContext));
        Assert.Null(orphanContext);
    }

    [Fact]
    public async Task TabCommands_EligibilityQuery_DoesNotChangeSelectionOrFocus()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity("pane-eligibility"));
        var provider = new MutableProvider();
        var tab = new ProviderTab(provider)
        {
            Id = "eligibility-tab",
            Title = "Eligibility",
            Entity = CreateWorkspaceEntity("eligibility-tab"),
        };
        var context = CreateContext(vm, pane, tab);
        var selectedPaneBefore = vm.SelectedWorkspacePane;
        var selectedTabBefore = pane.SelectedTab;

        var descriptor = Assert.Single(tab.TabCommands.GetCommands(context));
        _ = descriptor.Availability?.Invoke(context);

        Assert.Same(selectedPaneBefore, vm.SelectedWorkspacePane);
        Assert.Same(selectedTabBefore, pane.SelectedTab);
    }

    [Fact]
    public async Task TabCommands_ProviderInvalidates_UpdatesAvailabilityForSameContext()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var pane = new WorkspacePaneViewModel(CreateWorkspaceEntity("pane-invalidate"));
        var provider = new MutableProvider();
        var tab = new ProviderTab(provider)
        {
            Id = "invalidate-tab",
            Title = "Invalidate",
            Entity = CreateWorkspaceEntity("invalidate-tab"),
        };
        var context = CreateContext(vm, pane, tab);
        var changed = 0;
        provider.CommandsChanged += (_, _) => changed++;

        var before = provider.GetCommands(context).Single().Availability!(context);
        Assert.True(before.IsEnabled);

        provider.SetEnabled(false);

        Assert.Equal(1, changed);
        var after = provider.GetCommands(context).Single().Availability!(context);
        Assert.False(after.IsEnabled);
        Assert.Equal("Temporarily unavailable.", after.DisabledReason);
    }

    private static TabCommandContext CreateContextWithInvoker(
        MainWindowViewModel mainWindow,
        WorkspacePaneViewModel pane,
        WorkspaceTabViewModel tab,
        TabCommandInvoker invoker)
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
        return new TabCommandContext(mainWindow, pane, dock, document, tab, invoker);
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

    private sealed class RecordingErrorSink : ITabCommandErrorSink
    {
        public TabCommandContext? LastContext { get; private set; }

        public TabCommandDescriptor? LastCommand { get; private set; }

        public Exception? LastException { get; private set; }

        public void ReportFailure(TabCommandContext context, TabCommandDescriptor command, Exception exception)
        {
            this.LastContext = context;
            this.LastCommand = command;
            this.LastException = exception;
        }
    }

    private sealed class MutableProvider : ITabCommandProvider
    {
        private bool enabled = true;

        public IReadOnlyList<TabCommandDescriptor> GetCommands(TabCommandContext context)
            =>
            [
                new TabCommandDescriptor
                {
                    Id = "mutable",
                    Header = "Mutable",
                    Availability = _ => this.enabled
                        ? TabCommandAvailability.Enabled
                        : TabCommandAvailability.Disabled("Temporarily unavailable."),
                    ExecuteAsync = (_, _) => Task.FromResult(TabCommandResult.Success()),
                },
            ];

        public event EventHandler? CommandsChanged;

        public void SetEnabled(bool value)
        {
            this.enabled = value;
            this.CommandsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

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
