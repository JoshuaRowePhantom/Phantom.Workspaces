using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services.Navigation;
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.ViewModels;
using System.Reflection;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspaceNavigationActivationTests
{
    [AvaloniaTheory(Timeout = 30_000)]
    [InlineData("notification")]
    [InlineData("missing-hint")]
    [InlineData("stale-hint")]
    [InlineData("current")]
    [InlineData("next")]
    [InlineData("previous")]
    public async Task Navigate_InactiveWorkspace_ActivatesOuterAndExistingAgentDocument(string route)
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await vm.InitializeAsync();
        var house = await OpenWorkspaceAsync(vm, "House");
        var agent = new AgentSessionWorkspaceTabViewModel { Id = "house-agent", Title = "House Agent Session" };
        await vm.OpenTabAsync(agent);
        var houseDoc = Assert.IsType<WorkspaceDocument>(house.GetDocumentForTab(agent.Id));
        var other = await OpenWorkspaceAsync(vm, "Phantom.Workspaces");
        var factory = GetFactory(vm);
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            if (route == "current")
                factory.SetActiveDockable(factory.GetPaneDocument(house.Id)!);
            else
                factory.SetActiveDockable(factory.GetPaneDocument(other.Id)!);
            Dispatcher.UIThread.RunJobs();
            var paneCount = vm.WorkspacePanes.Count;
            var tabCount = house.Tabs.Count;
            var notification = new Notification(
                new TabDescriptor { TabId = agent.Id, WorkspaceId = house.Id }, "Agent finished", "ready",
                DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting);
            vm.NotificationService.Notify(notification);
            Dispatcher.UIThread.RunJobs();

            if (route is "missing-hint" or "stale-hint")
            {
                Assert.True(await vm.ActivateTabByRequestAsync(new NavigationRequest(
                    UiPath.ForTab(route == "missing-hint" ? null : other.Id, agent.Id))));
            }
            else if (route is "next" or "previous")
            {
                await ExecuteAndWaitForReadAsync(vm, agent.Id, () =>
                    (route == "next" ? vm.NavigateNextNotificationCommand : vm.NavigatePreviousNotificationCommand).Execute(null));
            }
            else
            {
                var row = Assert.Single(vm.NotificationsViewModel!.Rows, r => r.TabKey == agent.Id);
                row.NavigateCommand.Execute(null);
                await Assert.IsType<AsyncRelayCommand>(row.NavigateCommand).LastExecutionTask!;
            }
            Dispatcher.UIThread.RunJobs();

            AssertVisibleTarget(window, vm, house, houseDoc);
            Assert.Equal(paneCount, vm.WorkspacePanes.Count);
            Assert.Equal(tabCount, house.Tabs.Count);
            Assert.Same(agent, houseDoc.TabViewModel);
            Assert.Same(houseDoc, house.GetDocumentForTab(agent.Id));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task Navigate_DeferredMaterialization_WaitsForActivationBeforeReadAndHistory()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await vm.InitializeAsync();
        var house = await OpenWorkspaceAsync(vm, "House");
        var dock = MainWindowIntegrationTests.FindDocumentDockIn(house.ContentLayout!)!;
        Assert.IsType<WorkspaceContentDock>(dock).ItemsSource = null;
        var agent = new AgentSessionWorkspaceTabViewModel { Id = "deferred-agent", Title = "Deferred Agent" };
        house.Tabs.Add(agent);
        var other = await OpenWorkspaceAsync(vm, "Phantom.Workspaces");
        var factory = GetFactory(vm);
        factory.SetActiveDockable(factory.GetPaneDocument(other.Id)!);
        vm.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = agent.Id, WorkspaceId = house.Id }, "Done", "ready",
            DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));
        var history = new NavigationHistoryService();
        var navigator = new MainWindowTabNavigator(vm, history, vm.NotificationService);
        var navigation = navigator.NavigateAsync(
            new NavigationTarget { Path = UiPath.ForTab(house.Id, agent.Id) },
            new NavigationOptions { PushHistory = true });

        Assert.False(navigation.IsCompleted);
        Assert.Empty(history.Entries);
        Assert.False(Assert.Single(vm.NotificationService.Notifications, n => n.TabKey == agent.Id).IsRead);
        factory.SetActiveDockable(factory.GetPaneDocument(other.Id)!);
        var document = new WorkspaceDocument(agent) { Owner = dock };
        dock.VisibleDockables!.Add(document);
        house.RegisterDocument(agent.Id, document);

        Assert.True(await navigation);
        Assert.Same(factory.GetPaneDocument(house.Id),
            ((IDock)factory.GetPaneDocument(house.Id)!.Owner!).ActiveDockable);
        Assert.Same(document, dock.ActiveDockable);
        Assert.Same(document, house.ContentLayout!.FocusedDockable);
        Assert.True(Assert.Single(vm.NotificationService.Notifications, n => n.TabKey == agent.Id).IsRead);
        Assert.Equal(UiPath.ForTab(house.Id, agent.Id), Assert.Single(history.Entries).Path);
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task Navigate_DeferredTabRemoved_CompletesFalseWithoutSuccessSideEffects()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await vm.InitializeAsync();
        var house = await OpenWorkspaceAsync(vm, "House");
        Assert.IsType<WorkspaceContentDock>(
            MainWindowIntegrationTests.FindDocumentDockIn(house.ContentLayout!)!).ItemsSource = null;
        var agent = new AgentSessionWorkspaceTabViewModel { Id = "removed-agent", Title = "Agent" };
        house.Tabs.Add(agent);
        var navigation = house.NavigateToDocumentTabAsync(agent.Id);
        Assert.False(navigation.IsCompleted);
        house.Tabs.Remove(agent);
        Assert.False(await navigation);
        await agent.DisposeAsync();
    }

    [AvaloniaTheory(Timeout = 20_000)]
    [InlineData("missing")]
    [InlineData("failure")]
    [InlineData("closed")]
    public async Task Navigate_HydratingPaneCannotMaterialize_CompletesWithoutFalseSuccess(string outcome)
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await vm.InitializeAsync();
        var existing = await OpenWorkspaceAsync(vm, "House");
        var environment = (WorkspacePaneEnvironment)typeof(MainWindowViewModel)
            .GetField("workspacePaneEnvironment", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        await using var pane = new WorkspacePaneViewModel(existing.Entity, Guid.NewGuid().ToString(), environment: environment);
        vm.WorkspacePanes.Add(pane);
        pane.ContentLayout = GetFactory(vm).CreateWorkspaceContentLayout(pane);
        var navigation = pane.NavigateToDocumentTabAsync("absent", deferIfAbsent: true);
        Assert.False(navigation.IsCompleted);
        if (outcome == "failure")
        {
            var failure = new InvalidOperationException("Population failed");
            pane.SignalPopulated(failure);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => navigation));
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => pane.Populated));
        }
        else
        {
            if (outcome == "closed")
                await pane.DisposeAsync();
            else
                pane.SignalPopulated();
            Assert.False(await navigation);
        }
        vm.WorkspacePanes.Remove(pane);
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task Navigate_UnregisteredOuterDocument_ReturnsFalseWithoutReadOrHistory()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await vm.InitializeAsync();
        var pane = await OpenWorkspaceAsync(vm, "House");
        var agent = new AgentSessionWorkspaceTabViewModel { Id = "orphan-agent", Title = "Agent" };
        await vm.OpenTabAsync(agent);
        var outer = GetFactory(vm).GetPaneDocument(pane.Id)!;
        await OpenWorkspaceAsync(vm, "Phantom.Workspaces");
        outer.Owner = null;
        vm.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = agent.Id, WorkspaceId = pane.Id }, "Done", "ready",
            DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));
        var history = new NavigationHistoryService();
        var navigator = new MainWindowTabNavigator(vm, history, vm.NotificationService);

        Assert.False(await navigator.NavigateAsync(
            new NavigationTarget { Path = UiPath.ForTab(pane.Id, agent.Id) },
            new NavigationOptions { PushHistory = true }));
        Assert.Empty(history.Entries);
        Assert.False(Assert.Single(vm.NotificationService.Notifications, n => n.TabKey == agent.Id).IsRead);
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task Navigate_OrphanedInnerDocument_ReturnsFalse()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await vm.InitializeAsync();
        var house = await OpenWorkspaceAsync(vm, "House");
        var agent = new AgentSessionWorkspaceTabViewModel { Id = "orphan-inner", Title = "Agent" };
        await vm.OpenTabAsync(agent);
        var document = Assert.IsType<WorkspaceDocument>(house.GetDocumentForTab(agent.Id));
        document.Owner = null;

        Assert.False(await house.NavigateToDocumentTabAsync(agent.Id));
    }

    [AvaloniaFact(Timeout = 20_000)]
    public async Task Navigate_MissingTab_DoesNotChangeOuterPaneOrRecordSuccess()
    {
        await using var vm = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await vm.InitializeAsync();
        var house = await OpenWorkspaceAsync(vm, "House");
        var other = await OpenWorkspaceAsync(vm, "Phantom.Workspaces");
        var factory = GetFactory(vm);
        var active = factory.GetPaneDocument(other.Id)!;
        factory.SetActiveDockable(active);
        vm.NotificationService.Notify(new Notification(
            new TabDescriptor { TabId = "missing", WorkspaceId = house.Id }, "Done", "missing",
            DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting));
        var history = new NavigationHistoryService();
        var navigator = new MainWindowTabNavigator(vm, history, vm.NotificationService);

        Assert.False(await navigator.NavigateAsync(
            new NavigationTarget { Path = UiPath.ForTab(house.Id, "missing") },
            new NavigationOptions { PushHistory = true }));
        Assert.Same(active, ((IDock)active.Owner!).ActiveDockable);
        Assert.Same(other, vm.SelectedWorkspacePane);
        Assert.Empty(history.Entries);
        Assert.False(Assert.Single(vm.NotificationService.Notifications, n => n.TabKey == "missing").IsRead);
    }

    internal static WorkspaceDockFactory GetFactory(MainWindowViewModel vm) =>
        (WorkspaceDockFactory)typeof(MainWindowViewModel)
            .GetField("dockFactory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;

    internal static async Task ExecuteAndWaitForReadAsync(MainWindowViewModel vm, string tabId, Action execute)
    {
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler changed = (_, _) =>
        {
            if (vm.NotificationService.Notifications.Any(n => n.TabKey == tabId && n.IsRead))
                read.TrySetResult();
        };
        vm.NotificationService.NotificationsChanged += changed;
        try
        {
            execute();
            changed(null, EventArgs.Empty);
            await read.Task;
        }
        finally
        {
            vm.NotificationService.NotificationsChanged -= changed;
        }
    }

    internal static async Task<WorkspacePaneViewModel> OpenWorkspaceAsync(MainWindowViewModel vm, string title)
    {
        var id = new EntityId(Guid.NewGuid());
        await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(vm.EntityBroker, id, $$"""
            {"entity-id":"{{id.Value}}","entity-types":["entity","workspace"],
             "display-name":{"default":"{{title}}"},"regions":[]}
            """);
        await vm.OpenWorkspaceAsync(new GetEntityRequest { EntityId = id });
        var pane = vm.WorkspacePanes.Single(p => p.Id == id.ToString());
        await pane.Populated;
        return pane;
    }

    internal static void AssertVisibleTarget(
        MainWindow window, MainWindowViewModel vm, WorkspacePaneViewModel pane, WorkspaceDocument document)
    {
        var outer = Assert.IsType<WorkspacePaneDocument>(GetFactory(vm).GetPaneDocument(pane.Id));
        Assert.Same(outer, Assert.IsAssignableFrom<IDock>(outer.Owner).ActiveDockable);
        Assert.Same(outer, vm.Layout!.FocusedDockable);
        Assert.Same(pane, vm.SelectedWorkspacePane);
        var inner = Assert.IsAssignableFrom<IDock>(document.Owner);
        Assert.Same(document, inner.ActiveDockable);
        Assert.Same(document, pane.ContentLayout!.FocusedDockable);
        Assert.Contains(window.GetVisualDescendants().OfType<DockControl>(),
            c => c.IsVisible && ReferenceEquals(c.Layout, pane.ContentLayout));
        Assert.Contains(window.GetVisualDescendants(),
            c => c.IsVisible && ReferenceEquals(c.DataContext, document.TabViewModel));
    }
}
