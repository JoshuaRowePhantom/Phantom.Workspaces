using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using global::Dock.Model.Controls;
using global::Dock.Model.Core;
using Phantom.Dock.Avalonia.TabSwitching;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Web.Client;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.Services.Navigation;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class MainWindowViewModelTests
{
    [AvaloniaFact]
    public async Task MainWindow_AltShiftDigit_ActivatesWorkspacePaneHostTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceA = new EntityId("10810001-0000-4000-8000-000000000001");
        var workspaceB = new EntityId("10810001-0000-4000-8000-000000000002");
        await UpsertWorkspaceAsync(entityBroker, workspaceA, "ws-1081-a", "WS 1081 A");
        await UpsertWorkspaceAsync(entityBroker, workspaceB, "ws-1081-b", "WS 1081 B");
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceA });
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceB });

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { });
            var hostDockControl = FindHostDockControl(window);
            var hostDock = FindDocumentDockIn(hostDockControl.Layout!);
            Assert.NotNull(hostDock);
            var panes = hostDock!.VisibleDockables!.OfType<WorkspacePaneDocument>().ToList();
            Assert.True(panes.Count >= 2);

            var args = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.D2,
                KeyModifiers = KeyModifiers.Alt | KeyModifiers.Shift,
                Source = hostDockControl,
            };
            hostDockControl.RaiseEvent(args);

            Assert.True(args.Handled);
            Assert.Same(panes[1], hostDock.ActiveDockable);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void MainWindowViewModel_LegacyAltShortcutMembers_AreRemoved()
    {
        var vmType = typeof(MainWindowViewModel);
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        Assert.Empty(vmType.GetMember("AltShortcutLabelForIndex", flags));
        Assert.Empty(vmType.GetMember("RefreshTabAltShortcutLabels", flags));
        Assert.Empty(vmType.GetMember("RefreshActiveWorkspaceAltShortcutLabels", flags));
        Assert.Empty(vmType.GetMember("ComputeGlobalTabOrder", flags));
        Assert.Empty(vmType.GetMember("PropagateBadgeVisibility", flags));
        Assert.Empty(vmType.GetMember("IsAltHeld", flags));
        Assert.Empty(vmType.GetMember("IsShiftHeld", flags));
        Assert.Empty(vmType.GetMember("GoToTabAtIndexCommand", flags));
        Assert.Empty(vmType.GetMember("GoToWorkspacePaneAtIndexCommand", flags));

        var tabHeaderType = typeof(TabHeaderViewModel);
        Assert.Empty(tabHeaderType.GetMember("AltShortcutLabel", flags));
        Assert.Empty(tabHeaderType.GetMember("IsShortcutBadgeVisible", flags));
    }

    [AvaloniaFact]
    public async Task MainWindow_F7F8_NotificationNavigation_StillWorks()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        await viewModel.OpenTabAsync(new WebViewModel("https://notify-a.example.com") { Id = "notify-a", Title = "A" });
        await viewModel.OpenTabAsync(new WebViewModel("https://notify-b.example.com") { Id = "notify-b", Title = "B" });

        ActivateTab(viewModel, "notify-a");
        var now = DateTime.UtcNow;
        viewModel.NotificationService.Notify(new Notification(new TabDescriptor { TabId = "notify-a" }, "A", "n1", now.AddSeconds(-1), RunningState.Idle, NotificationState.Interesting));
        viewModel.NotificationService.Notify(new Notification(new TabDescriptor { TabId = "notify-b" }, "B", "n2", now, RunningState.Idle, NotificationState.Interesting));

        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            window.KeyPress(Key.F8, RawInputModifiers.None, PhysicalKey.F8, "");
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Equal("notify-b", viewModel.ActiveTabId);

            window.KeyPress(Key.F7, RawInputModifiers.None, PhysicalKey.F7, "");
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.Equal("notify-a", viewModel.ActiveTabId);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task MainWindowViewModel_OnRefreshTick_WhenWebDataThrows_DoesNotCrashAndFlagsNetworkStatus()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        Assert.NotNull(viewModel.ConnectionStatus);

        var failure = new WebDataAccessRequestException("relay answered 404", HttpStatusCode.NotFound);

        await viewModel.RefreshOnceAsync(() => throw failure);
        Dispatcher.UIThread.RunJobs();

        Assert.True(viewModel.ConnectionStatus!.HasRecentErrors);
        Assert.True(viewModel.ConnectionStatus.HasProblem);
        var recorded = Assert.Single(viewModel.ConnectionStatus.RecentErrors);
        Assert.Equal("relay answered 404", recorded.Message);
    }

    [AvaloniaFact]
    public async Task MainWindowViewModel_OnRefreshTick_WhenUnexpectedExceptionThrows_StillPropagates()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.RefreshOnceAsync(() => throw new InvalidOperationException("unexpected")));

        Dispatcher.UIThread.RunJobs();
        Assert.False(viewModel.ConnectionStatus!.HasRecentErrors);
    }

    private static MainWindowViewModel CreateTestMainWindowViewModel()
    {
        var sourceFactory = typeof(MainWindowIntegrationTests).GetMethod("CreateInMemoryRepositorySource", BindingFlags.NonPublic | BindingFlags.Static)!;
        var source = (RepositorySource)sourceFactory.Invoke(null, [])!;
        return new MainWindowViewModel(
            source,
            new WorkspacesConfiguration { SkipStartupWorkspace = true },
            new ProfileStore(Path.Combine(Path.GetTempPath(), "Phantom.Workspaces.Tests", Guid.NewGuid().ToString("N"), "profile.json")));
    }

    private static EntityBroker GetEntityBroker(MainWindowViewModel viewModel)
    {
        var property = typeof(MainWindowViewModel).GetProperty("EntityBroker", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (EntityBroker)property.GetValue(viewModel)!;
    }

    private static async Task UpsertWorkspaceAsync(EntityBroker entityBroker, EntityId id, string name, string title)
    {
        using var document = JsonDocument.Parse($$"""
        {
          "entity-id": "{{id.Value}}",
          "entity-types": ["entity", "workspace"],
          "names": [["tests", "workspaces", "{{name}}"]],
          "display-name": { "default": "{{title}}" },
          "regions": []
        }
        """);

        _ = await entityBroker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Test workspace" },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = id,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            });
    }

    private static DockControl CreateContentDockControl(IDock layout)
    {
        var dockControl = new DockControl { Layout = layout };
        DockTabSwitch.SetEnabled(dockControl, true);
        DockTabSwitch.SetBindings(dockControl, new DockTabSwitchBindings
        {
            new DockTabSwitchGestures
            {
                Modifiers = KeyModifiers.Alt,
                Keys = DockTabSwitchKeys.Digits,
                Scope = DockTabSwitchScope.FocusedDockOnly,
            },
        });

        return dockControl;
    }

    private static DockControl FindHostDockControl(Window window)
        => window.GetVisualDescendants()
            .OfType<DockControl>()
            .First(dc =>
                dc.Layout is not null
                && FindDocumentDockIn(dc.Layout)?.VisibleDockables?.OfType<WorkspacePaneDocument>().Any() == true);

    private static IDocumentDock? FindDocumentDockIn(IDockable dockable)
    {
        if (dockable is IDocumentDock dock)
        {
            return dock;
        }

        if (dockable is IDock parent && parent.VisibleDockables is not null)
        {
            foreach (var child in parent.VisibleDockables)
            {
                var match = FindDocumentDockIn(child);
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return null;
    }

    private static void ActivateTab(MainWindowViewModel viewModel, string tabId)
    {
        var dockFactoryField = typeof(MainWindowViewModel).GetField("dockFactory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dockFactory = (WorkspaceDockFactory)dockFactoryField.GetValue(viewModel)!;
        var document = viewModel.SelectedWorkspacePane.GetDocumentForTab(tabId)!;
        var documentDock = FindDocumentDockIn(viewModel.SelectedWorkspacePane.ContentLayout!)!;
        dockFactory.SetActiveDockable(document);
        dockFactory.SetFocusedDockable(documentDock, document);
    }

    // --- #1120: left-pane collapser (right/inner-edge), auto-expand on navigation ---------------

    [Fact]
    public async Task MainWindow_LeftPaneCollapser_TogglesLeftColumnToZero()
    {
        await using var viewModel = CreateTestMainWindowViewModel();

        Assert.False(viewModel.IsLeftPaneCollapsed);
        Assert.Equal(new GridLength(1, GridUnitType.Star), viewModel.LeftPaneColumnWidth);

        viewModel.IsLeftPaneCollapsed = true;

        Assert.True(viewModel.IsLeftPaneCollapsed);
        Assert.Equal(new GridLength(0, GridUnitType.Pixel), viewModel.LeftPaneColumnWidth);
    }

    [Fact]
    public async Task MainWindow_LeftPaneCollapser_ExpandRestoresColumnWidth()
    {
        await using var viewModel = CreateTestMainWindowViewModel();

        viewModel.IsLeftPaneCollapsed = true;
        Assert.Equal(new GridLength(0, GridUnitType.Pixel), viewModel.LeftPaneColumnWidth);

        viewModel.IsLeftPaneCollapsed = false;

        Assert.False(viewModel.IsLeftPaneCollapsed);
        // Restored to the default proportional (*) width of the left column.
        Assert.Equal(new GridLength(1, GridUnitType.Star), viewModel.LeftPaneColumnWidth);
    }

    [Fact]
    public async Task MainWindow_Navigate_AutoExpandsCollapsedLeftPane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();

        viewModel.IsLeftPaneCollapsed = true;
        Assert.True(viewModel.IsLeftPaneCollapsed);

        // Simulate the user selecting a different top-level view. The setter must auto-expand.
        var newView = new ViewDefinitionViewModel
        {
            Id = "nav-target",
            Title = "Nav Target",
            Description = string.Empty,
            IconGlyph = "◻",
        };
        viewModel.SelectedTopLevelView = newView;

        Assert.False(viewModel.IsLeftPaneCollapsed);
    }

    [Fact]
    public async Task MainWindow_LeftPaneCollapsed_PersistsUntilNavigateOrToggle()
    {
        await using var viewModel = CreateTestMainWindowViewModel();

        viewModel.IsLeftPaneCollapsed = true;

        // A non-navigation change (ShowHiddenItems) must not disturb the collapsed state.
        viewModel.ShowHiddenItems = !viewModel.ShowHiddenItems;
        Assert.True(viewModel.IsLeftPaneCollapsed);

        // Explicit toggle clears it.
        viewModel.IsLeftPaneCollapsed = false;
        Assert.False(viewModel.IsLeftPaneCollapsed);
    }

    [Fact]
    public void MainWindow_LeftPaneCollapser_MirrorsAgentChatCollapserClass()
    {
        // The left collapser in MainWindow uses the shared 'pane-collapser' class, and the
        // agent-chat TreeCollapseToggle uses the same class — the two collapsers share one style.
        var repoRoot = FindRepositoryRoot();
        var mainWindowXaml = File.ReadAllText(Path.Combine(
            repoRoot.FullName, "Phantom.Workspaces", "MainWindow.axaml"));
        var agentChatXaml = File.ReadAllText(Path.Combine(
            repoRoot.FullName, "Phantom.Workspaces.Agent.Gui", "Controls", "AgentChatEditorControl.axaml"));

        Assert.Contains("Classes=\"pane-collapser\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsLeftPaneCollapsed", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Classes=\"pane-collapser\"", agentChatXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowView_GenericViewTree_HasEntityCardTreeStickyClass()
    {
        // #1233: the generic schema-driven view TreeView (which renders views/git-workspaces) must
        // opt into sticky scroll by carrying the entity-card-tree-sticky class alongside
        // entity-card-tree and entity-card-tree-entity, so parent headers pin while scrolling.
        var repoRoot = FindRepositoryRoot();
        var mainWindowXaml = File.ReadAllText(Path.Combine(
            repoRoot.FullName, "Phantom.Workspaces", "MainWindow.axaml"));

        Assert.Contains(
            "Classes=\"entity-card-tree entity-card-tree-entity entity-card-tree-sticky\"",
            mainWindowXaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ToolbarBrainProgressBar_BindsIsIndeterminateToIsAnyAgentPulsating()
    {
        // #1305: the toolbar brain's animation must be driven by the aggregated per-agent
        // thinking state (IsAnyAgentPulsating), not by session presence (IsAnyRunning).
        var repoRoot = FindRepositoryRoot();
        var mainWindowXaml = File.ReadAllText(Path.Combine(
            repoRoot.FullName, "Phantom.Workspaces", "MainWindow.axaml"));

        Assert.Contains(
            "IsIndeterminate=\"{Binding RunningAgentBrain.IsAnyAgentPulsating",
            mainWindowXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "IsIndeterminate=\"{Binding RunningAgentBrain.IsAnyRunning",
            mainWindowXaml,
            StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task NavigateToHistoryEntry_ReplaysTargetTabViaTabNavigator_WithoutRePushingHistory()
    {
        // #1254: the Ctrl nav-stack popup replay path routes through the shared ITabNavigator with
        // PushHistory = false, so replaying a history entry activates the target tab without adding
        // a new entry (which would corrupt back/forward navigation).
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        await viewModel.OpenTabAsync(new WebViewModel("https://hist-a.example.com/") { Id = "hist-a", Title = "A" });
        await viewModel.OpenTabAsync(new WebViewModel("https://hist-b.example.com/") { Id = "hist-b", Title = "B" });
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var historyField = typeof(MainWindowViewModel).GetField(
            "navigationHistoryService", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var history = (Phantom.Workspaces.Services.Navigation.INavigationHistoryService)historyField.GetValue(viewModel)!;
        var entryCountBefore = history.Entries.Count;

        var targetIndex = -1;
        for (var i = 0; i < history.Entries.Count; i++)
        {
            if (history.Entries[i].Path.TabId == "hist-a")
            {
                targetIndex = i;
                break;
            }
        }

        Assert.True(targetIndex >= 0);

        await viewModel.NavigateToHistoryEntryAsync(targetIndex);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Equal("hist-a", viewModel.ActiveTabId);
        Assert.Equal(entryCountBefore, history.Entries.Count);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // NEW TESTS: #1341 navigation by request
    // ══════════════════════════════════════════════════════════════════════════

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_NavigateByRequest_ResolvesPaneByWorkspaceTabId_ThenDelegatesToPane()
    {
        // Test #11: ActivateTabByRequestAsync with WorkspaceTabId resolves the correct pane and activates the tab.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceIdA = new EntityId("11411001-0000-4000-8000-000000000001");
        var workspaceIdB = new EntityId("11411001-0000-4000-8000-000000000002");
        await UpsertWorkspaceAsync(entityBroker, workspaceIdA, "nav-test-a", "Nav Test A");
        await UpsertWorkspaceAsync(entityBroker, workspaceIdB, "nav-test-b", "Nav Test B");

        await viewModel.OpenWorkspaceAsync(new Phantom.Workspaces.Data.GetEntityRequest { EntityId = workspaceIdA });
        await viewModel.OpenWorkspaceAsync(new Phantom.Workspaces.Data.GetEntityRequest { EntityId = workspaceIdB });

        var paneA = viewModel.WorkspacePanes.Single(p => p.Id == workspaceIdA.ToString());
        var paneB = viewModel.WorkspacePanes.Single(p => p.Id == workspaceIdB.ToString());

        // Add a tab to pane A.
        viewModel.SelectedWorkspacePane = paneA;
        var tabInA = new WebViewModel("https://nav-a.example.com") { Id = "nav-tab-in-a", Title = "Tab in A" };
        await viewModel.OpenTabAsync(tabInA);

        var contentDockA = MainWindowIntegrationTests.FindDocumentDockIn(paneA.ContentLayout!);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDockA!, "nav-tab-in-a");

        // Switch to pane B.
        viewModel.SelectedWorkspacePane = paneB;
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Navigate to tab in pane A using the request API.
        var request = new Phantom.Workspaces.Services.Navigation.NavigationRequest(UiPath.ForTab(workspaceIdA.ToString(), "nav-tab-in-a"));
        var result = await viewModel.ActivateTabByRequestAsync(request);

        Assert.True(result);
        Assert.Equal(workspaceIdA.ToString(), viewModel.SelectedWorkspacePane.Id);
        var activeDoc = contentDockA!.ActiveDockable as WorkspaceDocument;
        Assert.NotNull(activeDoc);
        Assert.Equal("nav-tab-in-a", activeDoc!.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task MainWindowViewModel_NavigateByRequest_UnknownDocumentTabId_FallsBackToAllPanesOwnershipQuery()
    {
        // Test #12: When WorkspaceTabId is missing/invalid, the fallback searches all panes via OwnsDocumentTab.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("11411002-0000-4000-8000-000000000001");
        await UpsertWorkspaceAsync(entityBroker, workspaceId, "fallback-ws", "Fallback WS");
        await viewModel.OpenWorkspaceAsync(new Phantom.Workspaces.Data.GetEntityRequest { EntityId = workspaceId });

        var pane = viewModel.WorkspacePanes.Single(p => p.Id == workspaceId.ToString());
        var tab = new WebViewModel("https://fallback-tab.example.com") { Id = "fallback-tab", Title = "Fallback" };
        await viewModel.OpenTabAsync(tab);

        var contentDock = MainWindowIntegrationTests.FindDocumentDockIn(pane.ContentLayout!);
        await MainWindowIntegrationTests.WaitForWorkspaceTabAsync(contentDock!, "fallback-tab");

        // Navigate with an EMPTY WorkspaceTabId so the fallback path is exercised.
        var request = new Phantom.Workspaces.Services.Navigation.NavigationRequest(UiPath.ForTab(string.Empty, "fallback-tab"));
        var result = await viewModel.ActivateTabByRequestAsync(request);

        Assert.True(result);
        var activeDoc = contentDock!.ActiveDockable as WorkspaceDocument;
        Assert.NotNull(activeDoc);
        Assert.Equal("fallback-tab", activeDoc!.Id);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task NavigateToNotificationTab_WhenWorkspaceIdNonGuid_MaterializesOwningPane()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var pane = viewModel.SelectedWorkspacePane;
        var tab = new WebViewModel("https://non-guid.example.com") { Id = "non-guid-tab", Title = "Non GUID" };
        pane.Tabs.Add(tab);
        pane.ContentLayout = null;

        var result = await viewModel.ActivateTabByRequestAsync(
            new NavigationRequest(new UiPath(pane.Id, tab.Id)));

        Assert.True(result);
        Assert.NotNull(pane.ContentLayout);
        Assert.Same(pane, viewModel.SelectedWorkspacePane);
    }

    [AvaloniaFact]
    public async Task ViewPopulation_EntityDataChanged_UpdatesItemInPlaceWithoutRebuild()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var broker = GetEntityBroker(viewModel);

        var workspaceA = new EntityId("14500001-0000-4000-8000-000000000001");
        var workspaceB = new EntityId("14500001-0000-4000-8000-000000000002");
        await UpsertWorkspaceAsync(broker, workspaceA, "issue1450-a", "Issue1450 A");
        await UpsertWorkspaceAsync(broker, workspaceB, "issue1450-b", "Issue1450 B");
        await SelectWorkspacesViewAsync(viewModel);

        var entities = viewModel.CurrentViewPopulation.Entities;
        var roots = viewModel.CurrentViewPopulation.RootEntities;
        var before = Assert.Single(entities, vm => vm.EntityId == workspaceA.ToString());
        var structuralActions = TrackStructuralCollectionActions(entities, roots);

        await UpdateWorkspaceDisplayNameAsync(broker, workspaceA, "Issue1450 A Updated");
        await WaitForDisplayNameAsync(before, "Issue1450 A Updated");

        Assert.Same(before, Assert.Single(entities, vm => vm.EntityId == workspaceA.ToString()));
        Assert.Empty(structuralActions);
    }

    [AvaloniaFact]
    public async Task ViewPopulation_EntityAdded_InsertsSingleItem()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var broker = GetEntityBroker(viewModel);

        var workspaceA = new EntityId("14500002-0000-4000-8000-000000000001");
        var workspaceB = new EntityId("14500002-0000-4000-8000-000000000002");
        var workspaceC = new EntityId("14500002-0000-4000-8000-000000000003");
        await UpsertWorkspaceAsync(broker, workspaceA, "issue1450-add-a", "Issue1450 Add A");
        await UpsertWorkspaceAsync(broker, workspaceB, "issue1450-add-b", "Issue1450 Add B");
        await SelectWorkspacesViewAsync(viewModel);

        var entities = viewModel.CurrentViewPopulation.Entities;
        var roots = viewModel.CurrentViewPopulation.RootEntities;
        var referencesBefore = entities.ToDictionary(vm => vm.EntityId, vm => vm, StringComparer.Ordinal);
        var structuralActions = TrackStructuralCollectionActions(entities, roots);

        await UpsertWorkspaceAsync(broker, workspaceC, "issue1450-add-c", "Issue1450 Add C");
        await WaitForEntityInCollectionAsync(entities, workspaceC.ToString());

        Assert.Equal(2, structuralActions.Count(action => action == NotifyCollectionChangedAction.Add));
        Assert.DoesNotContain(structuralActions, action => action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove);
        Assert.Same(referencesBefore[workspaceA.ToString()], Assert.Single(entities, vm => vm.EntityId == workspaceA.ToString()));
        Assert.Same(referencesBefore[workspaceB.ToString()], Assert.Single(entities, vm => vm.EntityId == workspaceB.ToString()));
    }

    [AvaloniaFact]
    public async Task ViewPopulation_EntityDeleted_RemovesSingleItem()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var broker = GetEntityBroker(viewModel);

        var workspaceA = new EntityId("14500003-0000-4000-8000-000000000001");
        var workspaceB = new EntityId("14500003-0000-4000-8000-000000000002");
        var workspaceC = new EntityId("14500003-0000-4000-8000-000000000003");
        await UpsertWorkspaceAsync(broker, workspaceA, "issue1450-del-a", "Issue1450 Del A");
        await UpsertWorkspaceAsync(broker, workspaceB, "issue1450-del-b", "Issue1450 Del B");
        await UpsertWorkspaceAsync(broker, workspaceC, "issue1450-del-c", "Issue1450 Del C");
        await SelectWorkspacesViewAsync(viewModel);

        var entities = viewModel.CurrentViewPopulation.Entities;
        var roots = viewModel.CurrentViewPopulation.RootEntities;
        var referencesBefore = entities.ToDictionary(vm => vm.EntityId, vm => vm, StringComparer.Ordinal);
        var structuralActions = TrackStructuralCollectionActions(entities, roots);

        await DeleteEntityAsync(broker, workspaceB);
        await WaitForEntityAbsenceAsync(entities, workspaceB.ToString());

        Assert.Equal(2, structuralActions.Count(action => action == NotifyCollectionChangedAction.Remove));
        Assert.DoesNotContain(structuralActions, action => action == NotifyCollectionChangedAction.Reset);
        Assert.Same(referencesBefore[workspaceA.ToString()], Assert.Single(entities, vm => vm.EntityId == workspaceA.ToString()));
        Assert.Same(referencesBefore[workspaceC.ToString()], Assert.Single(entities, vm => vm.EntityId == workspaceC.ToString()));
    }

    [AvaloniaFact]
    public async Task ViewPopulation_EntityChange_PreservesSelectionAndExpansion()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var broker = GetEntityBroker(viewModel);

        var workspaceA = new EntityId("14500004-0000-4000-8000-000000000001");
        var workspaceB = new EntityId("14500004-0000-4000-8000-000000000002");
        var child = new EntityId("14500004-0000-4000-8000-000000000003");
        var related = new EntityId("14500004-0000-4000-8000-000000000004");
        await UpsertWorkspaceEntityTypeViewAsync(broker, new EntityId("14500004-0000-4000-8000-000000000005"));
        await UpsertWorkspaceAsync(broker, workspaceA, "issue1450-preserve-a", "Issue1450 Preserve A");
        await UpsertWorkspaceAsync(broker, workspaceB, "issue1450-preserve-b", "Issue1450 Preserve B");
        await UpsertNoteAsync(broker, child, "issue1450-preserve-child", "Issue1450 Preserve Child");
        await UpsertRelatedRelationshipAsync(broker, related, workspaceA, child, "issue1450-preserve-link");
        await SelectWorkspacesViewAsync(viewModel);

        var entities = viewModel.CurrentViewPopulation.Entities;
        var workspaceAVm = Assert.Single(entities, vm => vm.EntityId == workspaceA.ToString());
        var workspaceBVm = Assert.Single(entities, vm => vm.EntityId == workspaceB.ToString());
        var childVm = Assert.Single(entities, vm => vm.EntityId == child.ToString());
        workspaceAVm.IsExpanded = false;
        workspaceBVm.EntityCardNode.Card.IsSelected = true;

        await UpdateNoteDisplayNameAsync(broker, child, "Issue1450 Preserve Child Updated");
        await WaitForDisplayNameAsync(childVm, "Issue1450 Preserve Child Updated");

        Assert.False(workspaceAVm.IsExpanded);
        Assert.True(workspaceBVm.EntityCardNode.Card.IsSelected);
        Assert.Same(workspaceBVm, Assert.Single(entities, vm => vm.EntityId == workspaceB.ToString()));
    }

    [AvaloniaFact]
    public async Task ViewPopulation_NoMembershipChange_DoesNotMutateCollectionStructure()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var broker = GetEntityBroker(viewModel);

        var workspaceA = new EntityId("14500005-0000-4000-8000-000000000001");
        var workspaceB = new EntityId("14500005-0000-4000-8000-000000000002");
        await UpsertWorkspaceAsync(broker, workspaceA, "issue1450-nostruct-a", "Issue1450 NoStruct A");
        await UpsertWorkspaceAsync(broker, workspaceB, "issue1450-nostruct-b", "Issue1450 NoStruct B");
        await SelectWorkspacesViewAsync(viewModel);

        var entities = viewModel.CurrentViewPopulation.Entities;
        var roots = viewModel.CurrentViewPopulation.RootEntities;
        var beforeA = Assert.Single(entities, vm => vm.EntityId == workspaceA.ToString());
        var structuralActions = TrackStructuralCollectionActions(entities, roots);

        await UpdateWorkspaceDisplayNameAsync(broker, workspaceA, "Issue1450 NoStruct A Updated");
        await WaitForDisplayNameAsync(beforeA, "Issue1450 NoStruct A Updated");

        Assert.Empty(structuralActions);
    }

    [AvaloniaFact]
    public async Task ViewPopulation_ExistingItemReferences_ArePreservedAcrossRefresh()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        var broker = GetEntityBroker(viewModel);

        var workspaceA = new EntityId("14500006-0000-4000-8000-000000000001");
        var workspaceB = new EntityId("14500006-0000-4000-8000-000000000002");
        var workspaceC = new EntityId("14500006-0000-4000-8000-000000000003");
        await UpsertWorkspaceAsync(broker, workspaceA, "issue1450-ref-a", "Issue1450 Ref A");
        await UpsertWorkspaceAsync(broker, workspaceB, "issue1450-ref-b", "Issue1450 Ref B");
        await SelectWorkspacesViewAsync(viewModel);

        var entities = viewModel.CurrentViewPopulation.Entities;
        var beforeA = Assert.Single(entities, vm => vm.EntityId == workspaceA.ToString());
        var beforeB = Assert.Single(entities, vm => vm.EntityId == workspaceB.ToString());

        await UpsertWorkspaceAsync(broker, workspaceC, "issue1450-ref-c", "Issue1450 Ref C");
        await WaitForEntityInCollectionAsync(entities, workspaceC.ToString());
        await UpdateWorkspaceDisplayNameAsync(broker, workspaceB, "Issue1450 Ref B Updated");
        await WaitForDisplayNameAsync(beforeB, "Issue1450 Ref B Updated");

        Assert.Same(beforeA, Assert.Single(entities, vm => vm.EntityId == workspaceA.ToString()));
        Assert.Same(beforeB, Assert.Single(entities, vm => vm.EntityId == workspaceB.ToString()));
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Phantom.Workspaces.slnx")))
            {
                return current;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test base directory.");
    }

    private static async Task SelectWorkspacesViewAsync(MainWindowViewModel viewModel)
    {
        var workspacesView = Assert.Single(
            viewModel.TopLevelViews,
            static view => string.Equals(view.Title, "Workspaces", StringComparison.Ordinal));
        viewModel.SelectedTopLevelView = workspacesView;
        await InvokeApplySelectedViewAsync(viewModel);
    }

    private static async Task InvokeApplySelectedViewAsync(MainWindowViewModel viewModel)
    {
        var method = typeof(MainWindowViewModel).GetMethod(
            "ApplySelectedViewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await (Task)method!.Invoke(viewModel, [])!;
    }

    private static List<NotifyCollectionChangedAction> TrackStructuralCollectionActions(
        System.Collections.ObjectModel.ObservableCollection<ViewEntityViewModel> entities,
        System.Collections.ObjectModel.ObservableCollection<ViewEntityViewModel> roots)
    {
        var actions = new List<NotifyCollectionChangedAction>();
        void Capture(object? _, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action is NotifyCollectionChangedAction.Add
                or NotifyCollectionChangedAction.Remove
                or NotifyCollectionChangedAction.Move
                or NotifyCollectionChangedAction.Reset)
            {
                actions.Add(e.Action);
            }
        }

        entities.CollectionChanged += Capture;
        roots.CollectionChanged += Capture;
        return actions;
    }

    private static async Task WaitForDisplayNameAsync(ViewEntityViewModel viewEntity, string expectedDisplayName)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, PropertyChangedEventArgs e)
        {
            if (string.Equals(viewEntity.DisplayName, expectedDisplayName, StringComparison.Ordinal))
            {
                viewEntity.PropertyChanged -= Handler;
                tcs.TrySetResult();
            }
        }

        viewEntity.PropertyChanged += Handler;
        if (string.Equals(viewEntity.DisplayName, expectedDisplayName, StringComparison.Ordinal))
        {
            viewEntity.PropertyChanged -= Handler;
            return;
        }

        await tcs.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task WaitForEntityInCollectionAsync(
        System.Collections.ObjectModel.ObservableCollection<ViewEntityViewModel> entities,
        string entityId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, NotifyCollectionChangedEventArgs __)
        {
            if (entities.Any(vm => string.Equals(vm.EntityId, entityId, StringComparison.Ordinal)))
            {
                entities.CollectionChanged -= Handler;
                tcs.TrySetResult();
            }
        }

        entities.CollectionChanged += Handler;
        if (entities.Any(vm => string.Equals(vm.EntityId, entityId, StringComparison.Ordinal)))
        {
            entities.CollectionChanged -= Handler;
            return;
        }

        await tcs.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task WaitForEntityAbsenceAsync(
        System.Collections.ObjectModel.ObservableCollection<ViewEntityViewModel> entities,
        string entityId)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, NotifyCollectionChangedEventArgs __)
        {
            if (entities.All(vm => !string.Equals(vm.EntityId, entityId, StringComparison.Ordinal)))
            {
                entities.CollectionChanged -= Handler;
                tcs.TrySetResult();
            }
        }

        entities.CollectionChanged += Handler;
        if (entities.All(vm => !string.Equals(vm.EntityId, entityId, StringComparison.Ordinal)))
        {
            entities.CollectionChanged -= Handler;
            return;
        }

        await tcs.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private static async Task UpdateWorkspaceDisplayNameAsync(EntityBroker broker, EntityId id, string displayName)
    {
        await UpsertEntityAsync(
            broker,
            id,
            $$"""
            {
              "entity-id": "{{id.Value}}",
              "entity-types": ["entity", "workspace"],
              "names": [["tests", "workspaces", "update-{{id.Value}}"]],
              "display-name": { "default": "{{displayName}}" },
              "regions": []
            }
            """);
    }

    private static async Task UpdateNoteDisplayNameAsync(EntityBroker broker, EntityId id, string displayName)
    {
        await UpsertEntityAsync(
            broker,
            id,
            $$"""
            {
              "entity-id": "{{id.Value}}",
              "entity-types": ["entity", "note"],
              "names": [["notes", "update-{{id.Value}}"]],
              "display-name": { "default": "{{displayName}}" },
              "content": { "mime-type": "text/markdown", "content": { "text": "note" } }
            }
            """);
    }

    private static async Task DeleteEntityAsync(EntityBroker broker, EntityId id)
    {
        var current = (await broker.GetEntitiesAsync([id])).Single(entity => entity.EntityId == id);
        var result = await broker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Delete test entity" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = id,
                    EntityChangeMode = EntityChangeMode.Replace,
                    ConcurrencyTag = current.ConcurrencyTag,
                    Data = null,
                },
            ],
        });
        AssertNoFailedEntityResult(result);
    }

    private static Task UpsertWorkspaceEntityTypeViewAsync(EntityBroker broker, EntityId id) =>
        UpsertEntityAsync(
            broker,
            id,
            $$"""
            {
              "entity-id": "{{id.Value}}",
              "entity-types": ["entity", "entity-type-view"],
              "names": [["entity-type-views", "workspace"]],
              "display-name": { "default": "Workspace View" },
              "traverse-relationships": [
                { "relationship-type-ids": ["related"] }
              ]
            }
            """);

    private static Task UpsertNoteAsync(EntityBroker broker, EntityId id, string name, string displayName) =>
        UpsertEntityAsync(
            broker,
            id,
            $$"""
            {
              "entity-id": "{{id.Value}}",
              "entity-types": ["entity", "note"],
              "names": [["notes", "{{name}}"]],
              "display-name": { "default": "{{displayName}}" },
              "content": { "mime-type": "text/markdown", "content": { "text": "{{displayName}}" } }
            }
            """);

    private static Task UpsertRelatedRelationshipAsync(
        EntityBroker broker,
        EntityId relationshipId,
        EntityId left,
        EntityId right,
        string name) =>
        UpsertEntityAsync(
            broker,
            relationshipId,
            $$"""
            {
              "entity-id": "{{relationshipId.Value}}",
              "entity-types": ["entity", "related", "relationship"],
              "names": [["relationships", "{{name}}"]],
              "participants": { "entities": ["{{left.Value}}", "{{right.Value}}"] }
            }
            """);

    private static async Task UpsertEntityAsync(EntityBroker broker, EntityId id, string json)
    {
        using var document = JsonDocument.Parse(json);
        var current = (await broker.GetEntitiesAsync([id])).SingleOrDefault(entity => entity.EntityId == id);
        var result = await broker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Test upsert" },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = id,
                        EntityChangeMode = EntityChangeMode.Replace,
                        ConcurrencyTag = current?.ConcurrencyTag,
                        Data = document.RootElement.Clone(),
                    },
                ],
            });
        AssertNoFailedEntityResult(result);
    }

    private static void AssertNoFailedEntityResult(UpdateResult result)
    {
        var failure = result.EntityResults.FirstOrDefault(static entityResult => entityResult.UpdateState == UpdateState.Failed);
        Assert.True(
            failure is null,
            failure is null ? string.Empty : string.Join(" | ", failure.Errors.Select(static error => error.Message)));
    }
}
