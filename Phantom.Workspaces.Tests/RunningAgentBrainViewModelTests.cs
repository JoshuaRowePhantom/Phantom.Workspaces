using Avalonia.Headless.XUnit;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AgentSchema;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.ViewModels;
using Phantom.Workspaces.Testing.Gui;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class RunningAgentBrainViewModelTests
{
    private sealed class FakeRunningAgentChatTable : IRunningAgentChatTable
    {
        public ObservableCollection<RunningAgentChatWithEntityInfo> RunningSessions { get; } = [];

        public void AddSession(string sessionKey, string entityName = "", string? workspaceId = null, bool isSubAgent = false)
        {
            var chat = new RunningAgentChat(new AgentSessionId(sessionKey), null!) { IsSubAgent = isSubAgent };
            RunningSessions.Add(new RunningAgentChatWithEntityInfo(chat, entityName, null, workspaceId));
        }

        public void RemoveSession(string sessionKey)
        {
            var item = RunningSessions.FirstOrDefault(s =>
                string.Equals(s.SessionId.Value, sessionKey, StringComparison.Ordinal));
            if (item is not null)
                RunningSessions.Remove(item);
        }

        public Task<RunningAgentChatLease> AcquireAsync(
            AcquireAgentChatRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException("Not used in unit tests.");
    }

    private static AgentSessionWorkspaceTabViewModel CreateReadyTab(string id, string title, string? agentSessionId = null)
    {
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = id,
            Title = title,
            AgentSessionId = agentSessionId,
        };
        return tab;
    }

    private static RunningAgentBrainViewModel CreateBrainVm(
        FakeRunningAgentChatTable table,
        IEnumerable<AgentTabInfo> tabs,
        FakeTabNavigator? navigator = null)
    {
        var tabList = tabs.ToList();
        navigator ??= new FakeTabNavigator();

        return new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => tabList,
            navigator: navigator,
            dispatch: action => action());
    }

    [Fact]
    public void WithNoActiveSessions_IsAnyRunning_IsFalse()
    {
        var table = new FakeRunningAgentChatTable();
        var vm = CreateBrainVm(table, []);

        Assert.False(vm.IsAnyRunning);
    }

    [Fact]
    public void WithActiveSessions_IsAnyRunning_IsTrue()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent Session");

        var vm = CreateBrainVm(table, []);

        Assert.True(vm.IsAnyRunning);
    }

    [Fact]
    public void WithSessionAndMatchingTab_HasRow()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent Session");
        var tab = CreateReadyTab("tab-1", "Agent Session", agentSessionId: "session-1");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "My Workspace", tab)],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        Assert.Single(vm.Rows);
    }

    [Fact]
    public void WithSessionAndMatchingTab_RowShowsWorkspaceAndTabTitles()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "My Agent");
        var tab = CreateReadyTab("tab-1", "My Agent Tab", agentSessionId: "session-1");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Project Workspace", tab)],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        Assert.Equal("Project Workspace", row.WorkspacePaneTitle);
        Assert.Equal("My Agent Tab", row.TabTitle);
        Assert.True(row.HasOpenTab);
    }

    [Fact]
    public void WithNoActiveSessions_HasNoRows()
    {
        var table = new FakeRunningAgentChatTable();

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
    }

    [Fact]
    public void ToggleOpenCommand_TogglesIsOpen()
    {
        var table = new FakeRunningAgentChatTable();
        var vm = CreateBrainVm(table, []);

        Assert.False(vm.IsOpen);
        vm.ToggleOpenCommand.Execute(null);
        Assert.True(vm.IsOpen);
        vm.ToggleOpenCommand.Execute(null);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void SessionsChanged_RefreshesIsAnyRunning()
    {
        var table = new FakeRunningAgentChatTable();
        var vm = CreateBrainVm(table, []);

        Assert.False(vm.IsAnyRunning);

        table.AddSession("session-1", "Agent");

        Assert.True(vm.IsAnyRunning);
    }

    [Fact]
    public void SessionsChanged_RemovesRowWhenSessionDisappears()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent");
        var tab = CreateReadyTab("tab-1", "Agent", agentSessionId: "session-1");

        var returnTabs = true;
        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => returnTabs
                ? [new AgentTabInfo("pane-1", "Workspace", tab)]
                : [],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        Assert.Single(vm.Rows);

        returnTabs = false;
        table.RemoveSession("session-1");

        Assert.Empty(vm.Rows);
    }

    // ── #1198: workspace-pane close releases lease → row removed ────────────

    [Fact]
    public void RunningAgentBrainViewModel_WhenAgentTabDisposedOnPaneClose_ClearsRow()
    {
        // Simulates the observable effect of #1198's fix: when a workspace pane hosting an
        // agent-session tab is closed, cascaded disposal releases the RunningAgentChatLease,
        // which drops the entry from IRunningAgentChatFactory.RunningSessions. The brain
        // view-model must react by clearing its row and reporting IsAnyRunning = false.
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1198", "Agent 1198");
        var tab = CreateReadyTab("tab-1198", "Agent 1198", agentSessionId: "session-1198");

        var returnTabs = true;
        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => returnTabs
                ? [new AgentTabInfo("pane-1198", "Workspace 1198", tab)]
                : [],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        Assert.Single(vm.Rows);
        Assert.True(vm.IsAnyRunning);

        // Pane close cascade → tab.DisposeAsync → lease.DisposeAsync → factory removes session.
        returnTabs = false;
        table.RemoveSession("session-1198");

        Assert.Empty(vm.Rows);
        Assert.False(vm.IsAnyRunning);
    }

    [Fact]
    public void RowActivateCommand_DelegatesToTabNavigator()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-abc", "Agent");
        var tab = CreateReadyTab("tab-abc", "Agent", agentSessionId: "session-abc");
        var navigator = new FakeTabNavigator();

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-xyz", "Workspace", tab)],
            navigator: navigator,
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        row.ActivateCommand.Execute(null);

        var call = Assert.Single(navigator.Calls);
        Assert.Equal("tab-abc", call.Target.TabId);
        Assert.Equal("pane-xyz", call.Target.WorkspacePaneId);
        Assert.Equal("session-abc", call.Target.AgentSessionKey);
        Assert.True(call.Options.OpenEntityIfNoTab);
    }

    [Fact]
    public void RowActivateCommand_ClosesPopupBeforeNavigating()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent");
        var tab = CreateReadyTab("tab-1", "Agent", agentSessionId: "session-1");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace", tab)],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        vm.IsOpen = true;
        var row = Assert.Single(vm.Rows);
        row.ActivateCommand.Execute(null);

        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void WithNoSessionsAndNoTabs_HasNoRows_AndIsNotRunning()
    {
        var table = new FakeRunningAgentChatTable();
        var vm = CreateBrainVm(table, []);

        Assert.False(vm.IsAnyRunning);
        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
    }

    [Fact]
    public void Refresh_UpdatesRowsFromNewSessionList()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent 1");
        var tab1 = CreateReadyTab("tab-1", "Agent 1", agentSessionId: "session-1");
        var tab2 = CreateReadyTab("tab-2", "Agent 2", agentSessionId: "session-2");

        var currentTabs = new List<AgentTabInfo>
        {
            new AgentTabInfo("pane-1", "Workspace A", tab1),
        };

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => currentTabs,
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        Assert.Single(vm.Rows);

        table.AddSession("session-2", "Agent 2");
        currentTabs.Add(new AgentTabInfo("pane-1", "Workspace A", tab2));
        vm.Refresh();

        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public void WithNoMatchingTab_RowShowsFallbackLabel()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-orphan", "Orphaned Agent");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        Assert.False(row.HasOpenTab);
        Assert.Equal("Orphaned Agent", row.EntityName);
    }

    [Fact]
    public void WithNoMatchingTab_FallbackRowActivateCommand_DelegatesToTabNavigator()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-orphan", "Orphaned Agent");
        var navigator = new FakeTabNavigator();

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [],
            navigator: navigator,
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        row.ActivateCommand.Execute(null);

        var call = Assert.Single(navigator.Calls);
        Assert.Null(call.Target.TabId);
        Assert.Equal("session-orphan", call.Target.AgentSessionKey);
        Assert.True(call.Options.OpenEntityIfNoTab);
    }

    [Fact]
    public void WithNoMatchingTab_FallbackRowActivateCommand_ClosesPopup()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-orphan", "Orphaned Agent");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        vm.IsOpen = true;
        var row = Assert.Single(vm.Rows);
        row.ActivateCommand.Execute(null);

        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void RunningAgentBrain_ClickSessionWithNoOwningWorkspace_IsSafeNoOp()
    {
        // #1135: A fallback row whose session has no resolvable owning workspace
        // (WorkspaceId == null) must not throw and must route through the navigator's
        // entity-fallback path (TabId == null, OpenEntityIfNoTab == true) rather than a
        // tab activation, so the currently-active pane is not disturbed. When there is no
        // owning workspace, OpenAgentForSessionAsync is a safe no-op.
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-no-owner", "Orphaned Agent", workspaceId: null);
        var navigator = new FakeTabNavigator();

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [],
            navigator: navigator,
            dispatch: action => action());

        vm.IsOpen = true;
        var row = Assert.Single(vm.Rows);

        var exception = Record.Exception(() => row.ActivateCommand.Execute(null));
        Assert.Null(exception);

        var call = Assert.Single(navigator.Calls);
        Assert.Null(call.Target.TabId);
        Assert.Equal("session-no-owner", call.Target.AgentSessionKey);
        Assert.True(call.Options.OpenEntityIfNoTab);
        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void Refresh_AfterDispose_DoesNotThrow()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent Session");

        var vm = CreateBrainVm(table, []);

        vm.Dispose();
        vm.Refresh();

        // No exception should be thrown
    }

    // ── Issue #798: Verify no border styling ─────────────────────────────────

    [Fact]
    public void RunningAgentBrainViewModel_Row_HasNoBorderStyling()
    {
        // This test verifies that RunningAgentBrainControl.axaml doesn't wrap rows in Border elements
        // The AXAML should have Button elements directly in the ItemTemplate, not wrapped in <Border Classes="interactive-row">
        
        // Find the AXAML file relative to the solution root
        var currentDir = Directory.GetCurrentDirectory();
        var solutionRoot = currentDir;
        
        // Navigate up until we find the solution root (contains Phantom.Workspaces directory)
        while (!Directory.Exists(Path.Combine(solutionRoot, "Phantom.Workspaces")) && 
               Path.GetDirectoryName(solutionRoot) is string parent)
        {
            solutionRoot = parent;
        }

        var axamlPath = Path.Combine(solutionRoot, "Phantom.Workspaces", "Controls", "RunningAgentBrainControl.axaml");

        if (!File.Exists(axamlPath))
        {
            Assert.Fail($"Could not find RunningAgentBrainControl.axaml at {axamlPath}. Current directory: {currentDir}");
        }

        var axaml = File.ReadAllText(axamlPath);

        // Verify no Border wrapper with interactive-row class in the ItemTemplate
        Assert.DoesNotContain("<Border Classes=\"interactive-row\"", axaml, StringComparison.Ordinal);
        
        // Verify the header text is correct
        Assert.Contains("Running agents", axaml, StringComparison.Ordinal);
    }
    // ── Sorting by activity ───────────────────────────────────────────────────

    [Fact]
    public void Refresh_SortsRows_ByLastActivityAtDescending()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-A", "Agent A");
        table.AddSession("session-B", "Agent B");

        var vm = CreateBrainVm(table, []);
        Assert.Equal(2, vm.Rows.Count);

        // Give session-A an older timestamp so session-B should sort first.
        var rowA = vm.Rows.First(r => r.SessionKey == "session-A");
        rowA.UpdateLastActivityAt(DateTime.UtcNow - TimeSpan.FromSeconds(10));

        vm.Refresh();

        Assert.Equal("session-B", vm.Rows[0].SessionKey);
        Assert.Equal("session-A", vm.Rows[1].SessionKey);
    }

    [Fact]
    public void Rows_InitialOrder_MostRecentActivityFirst()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-old", "Old Agent");
        table.AddSession("session-new", "New Agent");

        var vm = CreateBrainVm(table, []);

        // Set an older timestamp on session-old to simulate it having had activity earlier.
        var rowOld = vm.Rows.First(r => r.SessionKey == "session-old");
        rowOld.UpdateLastActivityAt(DateTime.UtcNow - TimeSpan.FromSeconds(5));

        // Calling Refresh() triggers ResortRows() at the end.
        vm.Refresh();

        Assert.Equal("session-new", vm.Rows[0].SessionKey);
    }

    [Fact]
    public void Rows_StableOrder_WhenNoActivityChanges()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent 1");
        table.AddSession("session-2", "Agent 2");

        var vm = CreateBrainVm(table, []);

        // Capture the current order.
        var firstKey = vm.Rows[0].SessionKey;
        var secondKey = vm.Rows[1].SessionKey;

        // Multiple Refresh() calls without any activity changes must not reorder.
        vm.Refresh();
        vm.Refresh();

        Assert.Equal(firstKey, vm.Rows[0].SessionKey);
        Assert.Equal(secondKey, vm.Rows[1].SessionKey);
    }

    [Fact]
    public void ResortRows_MovesSessionWithNewerActivityToTop()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-A", "Agent A");
        table.AddSession("session-B", "Agent B");

        var vm = CreateBrainVm(table, []);

        // Give session-A an older timestamp; session-B should be first.
        var rowA = vm.Rows.First(r => r.SessionKey == "session-A");
        rowA.UpdateLastActivityAt(DateTime.UtcNow - TimeSpan.FromSeconds(5));
        vm.ResortRows();

        Assert.Equal("session-B", vm.Rows[0].SessionKey);

        // Simulate new activity on session-A.
        rowA.UpdateLastActivityAt(DateTime.UtcNow + TimeSpan.FromSeconds(5));
        vm.ResortRows();

        Assert.Equal("session-A", vm.Rows[0].SessionKey);
    }

    [Fact]
    public async Task Rows_ResortedToTop_WhenSessionReceivesHistoryItem()
    {
        var agentDefinitionJson =
            """
            {
              "kind": "prompt",
              "name": "test",
              "model": { "id": "test", "provider": "echo", "apiType": "Echo" },
              "tools": []
            }
            """;

        var definition = AgentDefinitionLoader.LoadAgentFromJson(agentDefinitionJson);

        var chatA = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest { AgentDefinition = definition });
        var chatB = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest { AgentDefinition = definition });

        await using var agentVmA = new AgentViewModel(chatA, "session-A", "", new ObservableLoggerFactory(), TaskScheduler.Default);
        await using var agentVmB = new AgentViewModel(chatB, "session-B", "", new ObservableLoggerFactory(), TaskScheduler.Default);

        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "tab-A", Title = "A", AgentSessionId = "session-A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "tab-B", Title = "B", AgentSessionId = "session-B" };
        tabA.SetReady(agentVmA, new ObservableLoggerFactory());
        tabB.SetReady(agentVmB, new ObservableLoggerFactory());

        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-A", "Agent A");
        table.AddSession("session-B", "Agent B");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () =>
            [
                new AgentTabInfo("pane-1", "Workspace", tabA),
                new AgentTabInfo("pane-1", "Workspace", tabB),
            ],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        // Give session-A an older activity timestamp so session-B is initially first.
        var rowA = vm.Rows.First(r => r.SessionKey == "session-A");
        rowA.UpdateLastActivityAt(DateTime.UtcNow - TimeSpan.FromSeconds(5));
        vm.ResortRows();

        Assert.Equal("session-B", vm.Rows[0].SessionKey);

        // Adding a history item to chatA fires History.CollectionChanged synchronously,
        // which the ViewModel handles by calling UpdateLastActivityAt + ResortRows.
        chatA.History.Add(new AgentChatHistoryItem
        {
            Role = Microsoft.Extensions.AI.ChatRole.Assistant,
            Contents = [new Microsoft.Extensions.AI.TextContent("hello")],
            Timestamp = DateTimeOffset.UtcNow,
        });

        Assert.Equal("session-A", vm.Rows[0].SessionKey);

        vm.Dispose();
    }

    [Fact]
    public async Task RunningAgentBrainViewModel_OnActivity_StampsRowLastActivityAtFromInjectedTimeProvider()
    {
        var agentDefinitionJson =
            """
            {
              "kind": "prompt",
              "name": "test",
              "model": { "id": "test", "provider": "echo", "apiType": "Echo" },
              "tools": []
            }
            """;

        var definition = AgentDefinitionLoader.LoadAgentFromJson(agentDefinitionJson);
        var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest { AgentDefinition = definition });

        await using var agentVm = new AgentViewModel(chat, "session-1", "", new ObservableLoggerFactory(), TaskScheduler.Default);

        var tab = new AgentSessionWorkspaceTabViewModel { Id = "tab-1", Title = "A", AgentSessionId = "session-1" };
        tab.SetReady(agentVm, new ObservableLoggerFactory());

        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent A");

        var start = new DateTimeOffset(2024, 5, 1, 9, 0, 0, TimeSpan.Zero);
        var fake = new FakeTimeProvider(start);

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace", tab)],
            navigator: new FakeTabNavigator(),
            dispatch: action => action(),
            timeProvider: fake);

        var row = vm.Rows.Single(r => r.SessionKey == "session-1");

        // The row's LastActivityAt starts at the fake clock's construction-time value.
        Assert.Equal(start.UtcDateTime, row.LastActivityAt);

        // Advance the fake clock to prove the activity stamp reads the injected provider
        // (not wall-clock time) at the moment the activity callback fires.
        fake.Advance(TimeSpan.FromMinutes(42));

        // Adding a history item fires History.CollectionChanged synchronously, which the
        // ViewModel handles by stamping LastActivityAt via the injected TimeProvider.
        chat.History.Add(new AgentChatHistoryItem
        {
            Role = Microsoft.Extensions.AI.ChatRole.Assistant,
            Contents = [new Microsoft.Extensions.AI.TextContent("hello")],
            Timestamp = DateTimeOffset.UtcNow,
        });

        Assert.Equal(fake.GetUtcNow().UtcDateTime, row.LastActivityAt);
        Assert.NotEqual(start.UtcDateTime, row.LastActivityAt);

        vm.Dispose();
    }

    // ── Issue #610: Tab matching tests ─────────────────────────────────────────

    [Fact]
    public void TabWithMatchingAgentSessionId_ShowsTabRow_NotFallback()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent Session");
        var tab = CreateReadyTab("tab-1", "Agent Tab", agentSessionId: "session-1");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace Pane", tab)],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        Assert.True(row.HasOpenTab);
        Assert.Equal("Workspace Pane", row.WorkspacePaneTitle);
        Assert.Equal("Agent Tab", row.TabTitle);
    }

    [Fact]
    public void TabWithNullAgentSessionId_ShowsFallbackRow()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent Session");
        var tab = CreateReadyTab("tab-1", "Agent Tab", agentSessionId: null);

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace Pane", tab)],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        Assert.False(row.HasOpenTab);
    }

    [Fact]
    public void SessionRegisteredBeforeTabReady_UpgradesToTabRow_AfterRefresh()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent Session");

        var currentTabs = new List<AgentTabInfo>();

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => currentTabs,
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        // Initially no tabs - should show fallback row
        var row = Assert.Single(vm.Rows);
        Assert.False(row.HasOpenTab);

        // Add tab and refresh
        var tab = CreateReadyTab("tab-1", "Agent Tab", agentSessionId: "session-1");
        currentTabs.Add(new AgentTabInfo("pane-1", "Workspace Pane", tab));
        vm.Refresh();

        // Row should now show tab
        row = Assert.Single(vm.Rows);
        Assert.True(row.HasOpenTab);
    }

    [Fact]
    public void TwoSessionsInDifferentPanes_BothShowTabRows()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-A", "Agent A");
        table.AddSession("session-B", "Agent B");

        var tabA = CreateReadyTab("tab-A", "Agent A Tab", agentSessionId: "session-A");
        var tabB = CreateReadyTab("tab-B", "Agent B Tab", agentSessionId: "session-B");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () =>
            [
                new AgentTabInfo("pane-1", "Workspace One", tabA),
                new AgentTabInfo("pane-2", "Workspace Two", tabB),
            ],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        Assert.Equal(2, vm.Rows.Count);
        var rowA = vm.Rows.FirstOrDefault(r => r.SessionKey == "session-A");
        var rowB = vm.Rows.FirstOrDefault(r => r.SessionKey == "session-B");

        Assert.NotNull(rowA);
        Assert.NotNull(rowB);
        Assert.True(rowA.HasOpenTab);
        Assert.True(rowB.HasOpenTab);
        Assert.Equal("Workspace One", rowA.WorkspacePaneTitle);
        Assert.Equal("Workspace Two", rowB.WorkspacePaneTitle);
    }

    [Fact]
    public void TabClosed_WhileSessionRunning_DowngradesToFallback()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent Session");
        var tab = CreateReadyTab("tab-1", "Agent Tab", agentSessionId: "session-1");

        var currentTabs = new List<AgentTabInfo>
        {
            new AgentTabInfo("pane-1", "Workspace Pane", tab),
        };

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => currentTabs,
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        // Initially has open tab
        var row = Assert.Single(vm.Rows);
        Assert.True(row.HasOpenTab);

        // Remove tab and refresh
        currentTabs.Clear();
        vm.Refresh();

        // Should downgrade to fallback
        row = Assert.Single(vm.Rows);
        Assert.False(row.HasOpenTab);
    }

    [Fact]
    public void Refresh_WithTabSessionIdMismatch_ShowsFallback()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-abc", "Agent Session ABC");
        var tab = CreateReadyTab("tab-1", "Agent Tab", agentSessionId: "session-xyz");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace Pane", tab)],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        Assert.False(row.HasOpenTab);
    }

    // ── Issue #1037: open-session no-matching-tab race (ineffective struct guard) ──

    private static AgentDefinition LoadEchoAgentDefinition()
    {
        var agentDefinitionJson =
            """
            {
              "kind": "prompt",
              "name": "test",
              "model": { "id": "test", "provider": "echo", "apiType": "Echo" },
              "tools": []
            }
            """;
        return AgentDefinitionLoader.LoadAgentFromJson(agentDefinitionJson);
    }

    private static AgentChatHistoryItem MakeRunningItem()
        => new()
        {
            Role = Microsoft.Extensions.AI.ChatRole.Assistant,
            Contents = [new Microsoft.Extensions.AI.TextContent("thinking")],
        };

    [Fact]
    public async Task CreateAgentHandler_WhenOpeningSessionAndTabNotYetInAllAgentTabs_DoesNotThrowAndLeavesRowUnchanged()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = LoadEchoAgentDefinition() });
        await using var agentVm = new AgentViewModel(chat, "session-1", "", new ObservableLoggerFactory(), TaskScheduler.Default);

        var tab = new AgentSessionWorkspaceTabViewModel { Id = "tab-1", Title = "Agent Tab", AgentSessionId = "session-1" };
        tab.SetReady(agentVm, new ObservableLoggerFactory());

        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent");

        // The tab is present at construction so the per-agent handler subscribes.
        var currentTabs = new List<AgentTabInfo> { new("pane-1", "Workspace", tab) };
        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => currentTabs,
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        var thinkingBefore = row.IsThinking;

        // Simulate the open-session transient: the tab is momentarily not resolvable in
        // getAllAgentTabs() while the running process loop raises IsChatRunning.
        currentTabs.Clear();

        var exception = Record.Exception(() => chat.CreateRunningItem(MakeRunningItem()));

        Assert.Null(exception); // No NRE — the struct-default no-match is a safe no-op.
        Assert.Equal(thinkingBefore, row.IsThinking);

        vm.Dispose();
    }

    [Fact]
    public async Task CreateAgentHandler_WhenNoTabMatchesSession_LeavesRowIsThinkingUnchanged()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = LoadEchoAgentDefinition() });
        await using var agentVm = new AgentViewModel(chat, "session-1", "", new ObservableLoggerFactory(), TaskScheduler.Default);

        var tab = new AgentSessionWorkspaceTabViewModel { Id = "tab-1", Title = "Agent Tab", AgentSessionId = "session-1" };
        tab.SetReady(agentVm, new ObservableLoggerFactory());

        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent");

        var currentTabs = new List<AgentTabInfo> { new("pane-1", "Workspace", tab) };
        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => currentTabs,
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        Assert.False(row.IsThinking);

        // No matching tab when the handler runs → the row must not be mutated.
        currentTabs.Clear();
        chat.CreateRunningItem(MakeRunningItem());

        Assert.False(row.IsThinking);

        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task CreateAgentHandler_WhenTabMatches_UpdatesRowIsThinkingFromAgent()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = LoadEchoAgentDefinition() });
        await using var agentVm = new AgentViewModel(chat, "session-1", "", new ObservableLoggerFactory(), TaskScheduler.Default);

        var tab = new AgentSessionWorkspaceTabViewModel { Id = "tab-1", Title = "Agent Tab", AgentSessionId = "session-1" };
        tab.SetReady(agentVm, new ObservableLoggerFactory());

        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace", tab)],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        Assert.False(row.IsThinking);

        // A running item makes the agent's IsChatRunning true; the handler resolves the matching
        // tab and reflects it onto the row.
        chat.CreateRunningItem(MakeRunningItem());

        Assert.True(row.IsThinking);

        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task CreateAgentHandler_WhenAgentRaisesFromNonUiThread_MarshalsThroughDispatch()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = LoadEchoAgentDefinition() });
        await using var agentVm = new AgentViewModel(chat, "session-1", "", new ObservableLoggerFactory(), TaskScheduler.Default);

        var tab = new AgentSessionWorkspaceTabViewModel { Id = "tab-1", Title = "Agent Tab", AgentSessionId = "session-1" };
        tab.SetReady(agentVm, new ObservableLoggerFactory());

        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent");

        var dispatchCount = 0;
        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace", tab)],
            navigator: new FakeTabNavigator(),
            dispatch: action => { dispatchCount++; action(); });

        // The constructor's Refresh runs inline (not via dispatch), so no dispatch yet.
        var dispatchBefore = dispatchCount;

        chat.CreateRunningItem(MakeRunningItem());

        // The IsChatRunning handler body must be marshalled through dispatch.
        Assert.True(dispatchCount > dispatchBefore, "Expected the agent handler to run through dispatch.");

        vm.Dispose();
    }

    [Fact]
    public async Task CreateAgentHandler_WhenSessionHasNoResolvableTab_DoesNotThrowAndDoesNotMutate()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = LoadEchoAgentDefinition() });
        await using var agentVm = new AgentViewModel(chat, "session-1", "", new ObservableLoggerFactory(), TaskScheduler.Default);

        var tab = new AgentSessionWorkspaceTabViewModel { Id = "tab-1", Title = "Agent Tab", AgentSessionId = "session-1" };
        tab.SetReady(agentVm, new ObservableLoggerFactory());

        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent");

        // getAllAgentTabs always yields a tab whose session id never matches → no resolvable tab.
        var mismatchTab = new AgentSessionWorkspaceTabViewModel { Id = "tab-x", Title = "Other", AgentSessionId = "other-session" };
        var currentTabs = new List<AgentTabInfo> { new("pane-1", "Workspace", tab) };
        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => currentTabs,
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        var thinkingBefore = row.IsThinking;

        // Swap to a non-matching tab set (session-1 not resolvable) before raising.
        currentTabs.Clear();
        currentTabs.Add(new AgentTabInfo("pane-1", "Workspace", mismatchTab));

        var exception = Record.Exception(() => chat.CreateRunningItem(MakeRunningItem()));

        Assert.Null(exception);
        Assert.Equal(thinkingBefore, row.IsThinking);

        vm.Dispose();
    }

    [Fact]
    public async Task SubscribeRow_AfterTabCloseOrAgentChange_HandlerNoLongerFires()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = LoadEchoAgentDefinition() });
        await using var agentVm = new AgentViewModel(chat, "session-1", "", new ObservableLoggerFactory(), TaskScheduler.Default);

        var tab = new AgentSessionWorkspaceTabViewModel { Id = "tab-1", Title = "Agent Tab", AgentSessionId = "session-1" };
        tab.SetReady(agentVm, new ObservableLoggerFactory());

        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent");

        var currentTabs = new List<AgentTabInfo> { new("pane-1", "Workspace", tab) };
        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => currentTabs,
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        // Close the tab while the session keeps running: Refresh downgrades to a fallback row and
        // unsubscribes the per-agent handler.
        currentTabs.Clear();
        vm.Refresh();
        var row = Assert.Single(vm.Rows);
        Assert.False(row.HasOpenTab);
        row.IsThinking = false;

        // The now-unsubscribed handler must not fire into the stale row.
        chat.CreateRunningItem(MakeRunningItem());

        Assert.False(row.IsThinking);

        vm.Dispose();
    }

    [Fact]
    public void WithTopLevelSessionOnly_HasRowForTopLevelAgent()
    {
        // Issue #1150: top-level agents appear in RunningSessions and produce a row.
        var table = new FakeRunningAgentChatTable();
        table.AddSession("top-level-1", "Top Level Agent");
        var tab = CreateReadyTab("tab-1", "Top Level Agent", agentSessionId: "top-level-1");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace", tab)],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        Assert.Equal("top-level-1", row.SessionKey);
    }

    [Fact]
    public void WithSubAgentRunningUnderTopLevel_ListsOnlyTopLevelAgent()
    {
        // Issue #1150: sub-agents opt out at the factory via registerAsRunningAgent:false, so
        // they never appear in the table's RunningSessions. Only the top-level agent produces a row.
        var table = new FakeRunningAgentChatTable();
        table.AddSession("top-level-1", "Top Level Agent");
        // Sub-agent intentionally NOT added: registerAsRunningAgent:false at the source.
        var tab = CreateReadyTab("tab-1", "Top Level Agent", agentSessionId: "top-level-1");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace", tab)],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        Assert.Equal("top-level-1", row.SessionKey);
    }

    [Fact]
    public void WithOnlySubAgentSessions_HasNoRows()
    {
        // Issue #1150: if every running session is a dispatcher-created sub-agent, they all opt
        // out of RunningSessions, so the popup shows the empty state.
        var table = new FakeRunningAgentChatTable();
        // No sessions added: sub-agents don't register.

        var vm = CreateBrainVm(table, []);

        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
    }

    [Fact]
    public void WithOnlySubAgentSessions_IsAnyRunning_IsFalse()
    {
        // Issue #1150: IsAnyRunning is driven off RunningSessions.Count. When all live sessions are
        // sub-agents that opted out of registration, IsAnyRunning must remain false.
        var table = new FakeRunningAgentChatTable();

        var vm = CreateBrainVm(table, []);

        Assert.False(vm.IsAnyRunning);
    }

    [Fact]
    public void Refresh_AfterRestartWithRestoredSubAgents_ShowsOnlyParentRow()
    {
        // Issue #1205: after a restart-restore with one parent tab and N sub-agents in the
        // running-sessions table, the flyout must show exactly one row (the parent) — no
        // "No Open Tab" pollution rows for the restored sub-agents.
        var table = new FakeRunningAgentChatTable();
        table.AddSession("parent-1205", "Parent Chat");
        table.AddSession("child-a", "", isSubAgent: true);
        table.AddSession("child-b", "", isSubAgent: true);
        table.AddSession("child-c", "", isSubAgent: true);

        var parentTab = CreateReadyTab("tab-parent", "Parent Chat", agentSessionId: "parent-1205");
        var vm = CreateBrainVm(table, [new AgentTabInfo("pane-1", "Workspace", parentTab)]);

        var row = Assert.Single(vm.Rows);
        Assert.Equal("parent-1205", row.SessionKey);
        Assert.True(row.HasOpenTab);
    }

    [Fact]
    public void Refresh_SessionWithoutTabAndBackingChatIsSubAgent_IsNotShownAsFallbackRow()
    {
        // Issue #1205 Fix 2 (defensive): even if a sub-agent leaks into RunningSessions,
        // the view model must skip it instead of rendering a "No Open Tab" fallback row.
        var table = new FakeRunningAgentChatTable();
        table.AddSession("leaked-sub", "Leaked Sub-agent", isSubAgent: true);

        var vm = CreateBrainVm(table, []);

        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void Refresh_TopLevelSessionWithoutTab_StillShownAsFallbackRow()
    {
        // Issue #1205 Fix 2 must not over-filter: a legitimate top-level running chat that has
        // lost its tab (e.g. pane closed) is still shown as "No Open Tab".
        var table = new FakeRunningAgentChatTable();
        table.AddSession("orphan-top", "Orphaned Top-level", isSubAgent: false);

        var vm = CreateBrainVm(table, []);

        var row = Assert.Single(vm.Rows);
        Assert.Equal("orphan-top", row.SessionKey);
        Assert.False(row.HasOpenTab);
    }

    // ── Issue #1305: IsAnyAgentPulsating tracks per-row IsThinking ─────────────

    [Fact]
    public void RunningAgents_WhenNoSessions_BrainDoesNotPulsate()
    {
        var table = new FakeRunningAgentChatTable();
        var vm = CreateBrainVm(table, []);

        Assert.False(vm.IsAnyAgentPulsating);
    }

    [Fact]
    public async Task RunningAgents_WhenAllAgentsIdle_BrainDoesNotPulsate()
    {
        var chatA = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest { AgentDefinition = LoadEchoAgentDefinition() });
        var chatB = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest { AgentDefinition = LoadEchoAgentDefinition() });
        await using var agentVmA = new AgentViewModel(chatA, "session-A", "", new ObservableLoggerFactory(), TaskScheduler.Default);
        await using var agentVmB = new AgentViewModel(chatB, "session-B", "", new ObservableLoggerFactory(), TaskScheduler.Default);
        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "tab-A", Title = "A", AgentSessionId = "session-A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "tab-B", Title = "B", AgentSessionId = "session-B" };
        tabA.SetReady(agentVmA, new ObservableLoggerFactory());
        tabB.SetReady(agentVmB, new ObservableLoggerFactory());
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-A", "A");
        table.AddSession("session-B", "B");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace", tabA), new AgentTabInfo("pane-1", "Workspace", tabB)],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        Assert.True(vm.IsAnyRunning);
        Assert.False(vm.IsAnyAgentPulsating);

        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task RunningAgents_WhenAnyAgentPulsating_BrainPulsates()
    {
        var chatA = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest { AgentDefinition = LoadEchoAgentDefinition() });
        var chatB = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest { AgentDefinition = LoadEchoAgentDefinition() });
        await using var agentVmA = new AgentViewModel(chatA, "session-A", "", new ObservableLoggerFactory(), TaskScheduler.Default);
        await using var agentVmB = new AgentViewModel(chatB, "session-B", "", new ObservableLoggerFactory(), TaskScheduler.Default);
        var tabA = new AgentSessionWorkspaceTabViewModel { Id = "tab-A", Title = "A", AgentSessionId = "session-A" };
        var tabB = new AgentSessionWorkspaceTabViewModel { Id = "tab-B", Title = "B", AgentSessionId = "session-B" };
        tabA.SetReady(agentVmA, new ObservableLoggerFactory());
        tabB.SetReady(agentVmB, new ObservableLoggerFactory());
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-A", "A");
        table.AddSession("session-B", "B");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace", tabA), new AgentTabInfo("pane-1", "Workspace", tabB)],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        Assert.False(vm.IsAnyAgentPulsating);

        chatA.CreateRunningItem(MakeRunningItem());

        Assert.True(vm.IsAnyAgentPulsating);

        vm.Dispose();
    }

    [AvaloniaFact]
    public async Task RunningAgents_WhenLastPulsatingAgentBecomesIdle_BrainStopsPulsating()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest { AgentDefinition = LoadEchoAgentDefinition() });
        await using var agentVm = new AgentViewModel(chat, "session-1", "", new ObservableLoggerFactory(), TaskScheduler.Default);
        var tab = new AgentSessionWorkspaceTabViewModel { Id = "tab-1", Title = "A", AgentSessionId = "session-1" };
        tab.SetReady(agentVm, new ObservableLoggerFactory());
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "A");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace", tab)],
            navigator: new FakeTabNavigator(),
            dispatch: action => action());

        var running = chat.CreateRunningItem(MakeRunningItem());
        Assert.True(vm.IsAnyAgentPulsating);

        // Completing the running item flips IsChatRunning → false.
        chat.CompleteRunningItem(running, writeToHistory: false);

        Assert.False(vm.IsAnyAgentPulsating);

        vm.Dispose();
    }

    [Fact]
    public void RunningAgents_WhenPulsatingRowRemoved_BrainStopsPulsating()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "A");
        var vm = CreateBrainVm(table, []);
        var row = Assert.Single(vm.Rows);
        row.IsThinking = true;
        Assert.True(vm.IsAnyAgentPulsating);

        table.RemoveSession("session-1");

        Assert.False(vm.IsAnyAgentPulsating);
        Assert.Empty(vm.Rows);

        vm.Dispose();
    }

    [Fact]
    public void RunningAgents_WhenFallbackRowSessionRunning_BrainDoesNotPulsate()
    {
        // Fallback rows (no tab / no agent) have no thinking signal and never contribute.
        var table = new FakeRunningAgentChatTable();
        table.AddSession("orphan", "Orphan");

        var vm = CreateBrainVm(table, []);

        Assert.Single(vm.Rows);
        Assert.False(vm.IsAnyAgentPulsating);

        vm.Dispose();
    }

    [Fact]
    public void RunningAgents_WhenOnlySubAgentSessionsPulsating_BrainDoesNotPulsate()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("sub-1", "Sub", isSubAgent: true);

        var vm = CreateBrainVm(table, []);

        Assert.False(vm.IsAnyRunning);
        Assert.False(vm.IsAnyAgentPulsating);

        vm.Dispose();
    }

    [Fact]
    public void RunningAgents_IsAnyAgentPulsating_RaisesPropertyChangedWhenTransitioning()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "A");
        var vm = CreateBrainVm(table, []);
        var row = Assert.Single(vm.Rows);
        var changes = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RunningAgentBrainViewModel.IsAnyAgentPulsating))
            {
                changes++;
            }
        };

        row.IsThinking = true;
        Assert.Equal(1, changes);
        Assert.True(vm.IsAnyAgentPulsating);

        row.IsThinking = false;
        Assert.Equal(2, changes);
        Assert.False(vm.IsAnyAgentPulsating);

        vm.Dispose();
    }
}
