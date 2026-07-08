using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using AgentSchema;
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
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? agentServices = null,
            string entityName = "",
            string? entityId = null,
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
}
