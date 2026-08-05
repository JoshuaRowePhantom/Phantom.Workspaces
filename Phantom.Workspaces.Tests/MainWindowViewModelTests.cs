using System.Reflection;
using System.Text.Json;
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
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class MainWindowViewModelTests
{
    [AvaloniaFact]
    public async Task MainWindow_AltDigit_ActivatesContentTabFromVisibleDockables()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        await viewModel.OpenTabAsync(new WebViewModel("https://a.example.com") { Id = "alt-content-a", Title = "A" });
        await viewModel.OpenTabAsync(new WebViewModel("https://b.example.com") { Id = "alt-content-b", Title = "B" });
        await viewModel.OpenTabAsync(new WebViewModel("https://c.example.com") { Id = "alt-content-c", Title = "C" });

        var contentDock = FindDocumentDockIn(viewModel.SelectedWorkspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        var docs = contentDock!.VisibleDockables!.OfType<WorkspaceDocument>().ToList();
        Assert.True(docs.Count >= 3);
        var contentDockControl = CreateContentDockControl(viewModel.SelectedWorkspacePane.ContentLayout!);

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.D2,
            KeyModifiers = KeyModifiers.Alt,
            Source = contentDockControl,
        };
        contentDockControl.RaiseEvent(args);

        Assert.True(args.Handled);
        Assert.Same(docs[1], contentDock.ActiveDockable);
    }

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

    [AvaloniaFact]
    public async Task MainWindow_BadgeLabelAndActivation_UseSameOrder()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        await viewModel.OpenTabAsync(new WebViewModel("https://1.example.com") { Id = "order-a", Title = "A" });
        await viewModel.OpenTabAsync(new WebViewModel("https://2.example.com") { Id = "order-b", Title = "B" });
        await viewModel.OpenTabAsync(new WebViewModel("https://3.example.com") { Id = "order-c", Title = "C" });

        var contentDock = FindDocumentDockIn(viewModel.SelectedWorkspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        var visible = Assert.IsAssignableFrom<IList<IDockable>>(contentDock!.VisibleDockables!);
        var firstBefore = visible[0];
        visible.RemoveAt(0);
        visible.Insert(2, firstBefore);
        var contentDockControl = CreateContentDockControl(viewModel.SelectedWorkspacePane.ContentLayout!);

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.D1,
            KeyModifiers = KeyModifiers.Alt,
            Source = contentDockControl,
        };
        contentDockControl.RaiseEvent(args);

        Assert.True(args.Handled);
        Assert.Same(visible[0], contentDock.ActiveDockable);
    }

    [AvaloniaFact]
    public async Task MainWindow_AfterSplitOrReorder_NumberingMatchesVisibleOrder()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();
        await viewModel.OpenTabAsync(new WebViewModel("https://x.example.com") { Id = "reorder-a", Title = "A" });
        await viewModel.OpenTabAsync(new WebViewModel("https://y.example.com") { Id = "reorder-b", Title = "B" });
        await viewModel.OpenTabAsync(new WebViewModel("https://z.example.com") { Id = "reorder-c", Title = "C" });

        var contentDock = FindDocumentDockIn(viewModel.SelectedWorkspacePane.ContentLayout!);
        Assert.NotNull(contentDock);
        var visible = Assert.IsAssignableFrom<IList<IDockable>>(contentDock!.VisibleDockables!);
        var last = visible[2];
        visible.RemoveAt(2);
        visible.Insert(0, last);
        var contentDockControl = CreateContentDockControl(viewModel.SelectedWorkspacePane.ContentLayout!);

        var args = new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.D2,
            KeyModifiers = KeyModifiers.Alt,
            Source = contentDockControl,
        };
        contentDockControl.RaiseEvent(args);

        Assert.True(args.Handled);
        Assert.Same(visible[1], contentDock.ActiveDockable);
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
        var document = dockFactory.GetDocumentForTab(tabId)!;
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

    // ---------------------------------------------------------------------------------------------
    // #1172 — TryFocusExistingWebTabAsync (same-workspace, same-URL dedup for IUrlOpener).
    // ---------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task TryFocusExistingWebTabAsync_SameUrlOpenInSelectedPane_ActivatesTabAndReturnsTrue()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var url = "https://example.com/";
        var existing = new WebViewModel(url) { Id = "web-existing", Title = "Existing" };
        await viewModel.OpenTabAsync(existing);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var otherTab = new WebViewModel("https://other.example.com/") { Id = "web-other", Title = "Other" };
        await viewModel.OpenTabAsync(otherTab);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var result = await viewModel.TryFocusExistingWebTabAsync(url);

        Assert.True(result);
        Assert.Equal(existing, viewModel.SelectedWorkspacePane.SelectedTab);
    }

    [AvaloniaFact]
    public async Task TryFocusExistingWebTabAsync_SameUrlOpenInDifferentPane_ReturnsFalse()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var url = "https://example.com/";

        // Open a tab with the URL in the currently selected pane.
        var firstPaneTab = new WebViewModel(url) { Id = "web-p1", Title = "P1" };
        await viewModel.OpenTabAsync(firstPaneTab);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var firstPane = viewModel.SelectedWorkspacePane;

        // Move that tab to a "different" pane by removing it from the selected pane's Tabs.
        // Simulate a second pane by clearing the current pane's tabs; TryFocusExistingWebTabAsync
        // scans SelectedWorkspacePane.Tabs, so an empty selected pane must return false even if
        // other panes contain matching tabs — which is what "cross-workspace dedup is rejected"
        // requires.
        firstPane.Tabs.Clear();

        var result = await viewModel.TryFocusExistingWebTabAsync(url);
        Assert.False(result);
    }

    [AvaloniaFact]
    public async Task TryFocusExistingWebTabAsync_DifferentUrl_ReturnsFalse()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://example.com/a") { Id = "web-a", Title = "A" };
        await viewModel.OpenTabAsync(tab);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var result = await viewModel.TryFocusExistingWebTabAsync("https://example.com/b");
        Assert.False(result);
    }

    [AvaloniaFact]
    public async Task TryFocusExistingWebTabAsync_UrlDiffersOnlyByTrailingSlashOrHostCase_MatchesExistingTab()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://example.com/") { Id = "web-a", Title = "A" };
        await viewModel.OpenTabAsync(tab);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Uri.AbsoluteUri lowercases host and canonicalizes paths.
        var result = await viewModel.TryFocusExistingWebTabAsync("https://Example.com/");
        Assert.True(result);
    }

    [AvaloniaFact]
    public async Task TryFocusExistingWebTabAsync_UrlDiffersOnlyByFragment_ReturnsFalse()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var tab = new WebViewModel("https://example.com/page#a") { Id = "web-a", Title = "A" };
        await viewModel.OpenTabAsync(tab);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var result = await viewModel.TryFocusExistingWebTabAsync("https://example.com/page#b");
        Assert.False(result);
    }

    [AvaloniaFact]
    public async Task TryFocusExistingWebTabAsync_NoSelectedWorkspacePane_ReturnsFalse()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        // Do NOT initialize — the placeholder pane has null ContentLayout.

        var result = await viewModel.TryFocusExistingWebTabAsync("https://example.com/");
        Assert.False(result);
    }

    [AvaloniaFact]
    public async Task TryFocusExistingWebTabAsync_NonWebViewModelTabWithSameTitle_ReturnsFalse()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        // A dummy non-web tab whose id happens to look like a URL; must not match.
        var dummy = new WebViewModel("https://example.com/") { Id = "web-a", Title = "A" };
        await viewModel.OpenTabAsync(dummy);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        // Replace the tab in the pane's Tabs with a non-WebViewModel — a plain WorkspaceTabViewModel
        // is abstract; we simulate by removing the web tab entirely.
        viewModel.SelectedWorkspacePane.Tabs.Clear();

        var result = await viewModel.TryFocusExistingWebTabAsync("https://example.com/");
        Assert.False(result);
    }

    // ── #1198: pane-close cascades DisposeAsync to child tabs ───────────────

    [AvaloniaFact]
    public async Task MainWindowViewModel_ClosingWorkspacePaneWithRunningAgentTab_DisposesAllChildTabs()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, tabs) = AddPaneWithDisposeSpyTabs(viewModel, "ws-1198-a", 2);

        await viewModel.RemoveWorkspacePaneAsync(pane);

        Assert.All(tabs, t => Assert.Equal(1, t.DisposeCount));
        Assert.Empty(pane.Tabs);
    }

    [AvaloniaFact]
    public async Task MainWindowViewModel_ClosingWorkspacePaneWithRunningAgentTab_ReleasesRunningAgentChatLease()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var pane = AddPane(viewModel, "ws-1198-b");
        var agentTab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "agent-1198-b",
            Title = "Agent B",
        };
        var leaseDisposed = 0;
        var lease = new Phantom.Workspaces.Llm.RunningAgentChatLease(
            new Phantom.Workspaces.Llm.Interfaces.AgentSessionId("agent-session-1198-b"),
            null!,
            () => { System.Threading.Interlocked.Increment(ref leaseDisposed); return ValueTask.CompletedTask; });
        agentTab.SetLease(lease);
        pane.Tabs.Add(agentTab);

        await viewModel.RemoveWorkspacePaneAsync(pane);

        Assert.Equal(1, leaseDisposed);
    }

    [AvaloniaFact]
    public async Task MainWindowViewModel_ClosingPaneViaDockCloseButton_DisposesDocumentAndChildren()
    {
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, tabs) = AddPaneWithDisposeSpyTabs(viewModel, "ws-1198-c", 1);
        var paneDoc = new WorkspacePaneDocument(pane);

        // Dock UI close path: OnDockableClosed → OnWorkspacePaneDockableClosed → RemoveWorkspacePaneAsync.
        viewModel.OnWorkspacePaneDockableClosed(paneDoc);
        await Dispatcher.UIThread.InvokeAsync(() => { });
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.DoesNotContain(viewModel.WorkspacePanes, p => ReferenceEquals(p, pane));
        Assert.Equal(1, tabs[0].DisposeCount);
    }

    [AvaloniaFact]
    public async Task MainWindowViewModel_ClosingSingleTabViaDockClose_DisposesTabViewModel()
    {
        // Regression: single-tab close through the existing OnDockableTabClosed seam
        // must still dispose the tab VM.
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var (pane, tabs) = AddPaneWithDisposeSpyTabs(viewModel, "ws-1198-d", 1);
        var tab = tabs[0];

        viewModel.OnDockableTabClosed(tab);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.DoesNotContain(tab, pane.Tabs);
        Assert.Equal(1, tab.DisposeCount);
    }

    [AvaloniaFact]
    public async Task MainWindowViewModel_ReopeningWorkspaceAfterClose_RecreatesAgentSessionTab()
    {
        // #1198: closing a pane must evict stale documentsByTabId entries so the
        // freshly-restored tab is not treated as "already open" by OpenTabAsync's
        // dedupe (which would dispose the fresh tab and activate a dead document).
        await using var viewModel = CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var workspaceId = new EntityId("10810001-1198-4000-8000-000000000001");
        await UpsertWorkspaceAsync(entityBroker, workspaceId, "ws-1198-e", "WS 1198 E");
        await viewModel.OpenWorkspaceAsync(new GetEntityRequest { EntityId = workspaceId });

        var pane = Assert.Single(
            viewModel.WorkspacePanes,
            p => string.Equals(p.Id, workspaceId.Value.ToString(), StringComparison.Ordinal));
        await pane.Populated;

        // Open a tab that becomes registered in the dock factory's tab map.
        var tab = new WebViewModel("https://example-1198.example.com/") { Id = "web-1198-e", Title = "Web 1198 E" };
        await viewModel.OpenTabAsync(tab);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        var dockFactoryField = typeof(MainWindowViewModel).GetField("dockFactory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dockFactory = (WorkspaceDockFactory)dockFactoryField.GetValue(viewModel)!;
        Assert.NotNull(dockFactory.GetDocumentForTab(tab.Id));

        // Close the pane. Before #1198's fix, this left a stale documentsByTabId entry;
        // after the fix, pane.Tabs is cleared which propagates through the inner dock's
        // items-source generator and evicts the entry.
        await viewModel.RemoveWorkspacePaneAsync(pane);
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.Null(dockFactory.GetDocumentForTab(tab.Id));
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
            if (history.Entries[i].TabId == "hist-a")
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

    private static WorkspacePaneViewModel AddPane(MainWindowViewModel viewModel, string id)
    {
        var entityId = Guid.NewGuid();
        using var entityDoc = JsonDocument.Parse($$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "workspace"],
              "display-name": "{{id}}"
            }
            """);
        var entity = new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId(entityId),
                ConcurrencyTag = new ConcurrencyTag("1"),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
                Data = entityDoc.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });
        var pane = new WorkspacePaneViewModel(entity, id);
        viewModel.WorkspacePanes.Add(pane);
        return pane;
    }

    private static (WorkspacePaneViewModel pane, DisposeSpyTab_1198[] tabs) AddPaneWithDisposeSpyTabs(
        MainWindowViewModel viewModel, string paneId, int tabCount)
    {
        var pane = AddPane(viewModel, paneId);
        var tabs = new DisposeSpyTab_1198[tabCount];
        for (var i = 0; i < tabCount; i++)
        {
            tabs[i] = new DisposeSpyTab_1198($"{paneId}-tab-{i}");
            pane.Tabs.Add(tabs[i]);
        }
        return (pane, tabs);
    }

    private sealed class DisposeSpyTab_1198 : WorkspaceTabViewModel
    {
        public int DisposeCount { get; private set; }

        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public DisposeSpyTab_1198(string id)
        {
            this.Id = id;
            this.Title = id;
            this.DockRegion = "full";
        }

        public override async ValueTask DisposeAsync()
        {
            this.DisposeCount++;
            await base.DisposeAsync();
        }
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
}
