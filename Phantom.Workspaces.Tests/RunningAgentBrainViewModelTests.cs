using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AgentSchema;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class RunningAgentBrainViewModelTests
{
    private sealed class FakeRunningAgentChatTable : IRunningAgentChatTable
    {
        public ObservableCollection<RunningAgentChatWithEntityInfo> RunningSessions { get; } = [];

        public void AddSession(string sessionKey, string entityName = "")
        {
            var chat = new RunningAgentChat(new AgentSessionId(sessionKey), null!);
            RunningSessions.Add(new RunningAgentChatWithEntityInfo(chat, entityName, null));
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
        List<(string tabId, string? paneId)>? activatedTabs = null,
        List<string>? openedSessions = null)
    {
        var tabList = tabs.ToList();
        activatedTabs ??= [];
        openedSessions ??= [];

        return new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => tabList,
            activateTab: (tabId, paneId) => activatedTabs.Add((tabId, paneId)),
            openAgentForSession: sessionKey => openedSessions.Add(sessionKey),
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
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
            dispatch: action => action());

        Assert.Single(vm.Rows);

        returnTabs = false;
        table.RemoveSession("session-1");

        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void RowActivateCommand_CallsActivateTab()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-abc", "Agent");
        var tab = CreateReadyTab("tab-abc", "Agent", agentSessionId: "session-abc");
        var activated = new List<(string tabId, string? paneId)>();

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-xyz", "Workspace", tab)],
            activateTab: (tabId, paneId) => activated.Add((tabId, paneId)),
            openAgentForSession: _ => { },
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        row.ActivateCommand.Execute(null);

        Assert.Single(activated);
        Assert.Equal("tab-abc", activated[0].tabId);
        Assert.Equal("pane-xyz", activated[0].paneId);
    }

    [Fact]
    public void RowActivateCommand_ClosesPopup()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent");
        var tab = CreateReadyTab("tab-1", "Agent", agentSessionId: "session-1");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace", tab)],
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        Assert.False(row.HasOpenTab);
        Assert.Equal("Orphaned Agent", row.EntityName);
    }

    [Fact]
    public void WithNoMatchingTab_FallbackRowActivateCommand_CallsOpenAgentForSession()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-orphan", "Orphaned Agent");
        var openedSessions = new List<string>();

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [],
            activateTab: (_, _) => { },
            openAgentForSession: sessionKey => openedSessions.Add(sessionKey),
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        row.ActivateCommand.Execute(null);

        Assert.Single(openedSessions);
        Assert.Equal("session-orphan", openedSessions[0]);
    }

    [Fact]
    public void WithNoMatchingTab_FallbackRowActivateCommand_ClosesPopup()
    {
        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-orphan", "Orphaned Agent");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [],
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
            dispatch: action => action());

        vm.IsOpen = true;
        var row = Assert.Single(vm.Rows);
        row.ActivateCommand.Execute(null);

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
        Assert.Contains("Running sub-agents", axaml, StringComparison.Ordinal);
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

        await using var agentVmA = new AgentViewModel(chatA, "session-A", "", new ObservableLoggerFactory());
        await using var agentVmB = new AgentViewModel(chatB, "session-B", "", new ObservableLoggerFactory());

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
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
        await using var agentVm = new AgentViewModel(chat, "session-1", "", new ObservableLoggerFactory());

        var tab = new AgentSessionWorkspaceTabViewModel { Id = "tab-1", Title = "Agent Tab", AgentSessionId = "session-1" };
        tab.SetReady(agentVm, new ObservableLoggerFactory());

        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent");

        // The tab is present at construction so the per-agent handler subscribes.
        var currentTabs = new List<AgentTabInfo> { new("pane-1", "Workspace", tab) };
        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => currentTabs,
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
        await using var agentVm = new AgentViewModel(chat, "session-1", "", new ObservableLoggerFactory());

        var tab = new AgentSessionWorkspaceTabViewModel { Id = "tab-1", Title = "Agent Tab", AgentSessionId = "session-1" };
        tab.SetReady(agentVm, new ObservableLoggerFactory());

        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent");

        var currentTabs = new List<AgentTabInfo> { new("pane-1", "Workspace", tab) };
        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => currentTabs,
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        Assert.False(row.IsThinking);

        // No matching tab when the handler runs → the row must not be mutated.
        currentTabs.Clear();
        chat.CreateRunningItem(MakeRunningItem());

        Assert.False(row.IsThinking);

        vm.Dispose();
    }

    [Fact]
    public async Task CreateAgentHandler_WhenTabMatches_UpdatesRowIsThinkingFromAgent()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = LoadEchoAgentDefinition() });
        await using var agentVm = new AgentViewModel(chat, "session-1", "", new ObservableLoggerFactory());

        var tab = new AgentSessionWorkspaceTabViewModel { Id = "tab-1", Title = "Agent Tab", AgentSessionId = "session-1" };
        tab.SetReady(agentVm, new ObservableLoggerFactory());

        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace", tab)],
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        Assert.False(row.IsThinking);

        // A running item makes the agent's IsChatRunning true; the handler resolves the matching
        // tab and reflects it onto the row.
        chat.CreateRunningItem(MakeRunningItem());

        Assert.True(row.IsThinking);

        vm.Dispose();
    }

    [Fact]
    public async Task CreateAgentHandler_WhenAgentRaisesFromNonUiThread_MarshalsThroughDispatch()
    {
        var chat = await AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest { AgentDefinition = LoadEchoAgentDefinition() });
        await using var agentVm = new AgentViewModel(chat, "session-1", "", new ObservableLoggerFactory());

        var tab = new AgentSessionWorkspaceTabViewModel { Id = "tab-1", Title = "Agent Tab", AgentSessionId = "session-1" };
        tab.SetReady(agentVm, new ObservableLoggerFactory());

        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent");

        var dispatchCount = 0;
        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace", tab)],
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
        await using var agentVm = new AgentViewModel(chat, "session-1", "", new ObservableLoggerFactory());

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
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
        await using var agentVm = new AgentViewModel(chat, "session-1", "", new ObservableLoggerFactory());

        var tab = new AgentSessionWorkspaceTabViewModel { Id = "tab-1", Title = "Agent Tab", AgentSessionId = "session-1" };
        tab.SetReady(agentVm, new ObservableLoggerFactory());

        var table = new FakeRunningAgentChatTable();
        table.AddSession("session-1", "Agent");

        var currentTabs = new List<AgentTabInfo> { new("pane-1", "Workspace", tab) };
        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => currentTabs,
            activateTab: (_, _) => { },
            openAgentForSession: _ => { },
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
}
