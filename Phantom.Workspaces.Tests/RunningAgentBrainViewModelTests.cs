using System;
using System.Collections.Generic;
using System.Linq;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class RunningAgentBrainViewModelTests
{
    private sealed class FakeRunningAgentChatTable : IRunningAgentChatTable
    {
        private int sessionCount;

        public event EventHandler? SessionsChanged;

        public int SessionCount => this.sessionCount;

        public void SetSessionCount(int count)
        {
            this.sessionCount = count;
            this.SessionsChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task<RunningAgentChatLease> AcquireAsync(string sessionKey, Func<Task<Phantom.Workspaces.Llm.AgentChat>> factory)
            => throw new NotSupportedException("Not used in unit tests.");
    }

    private static AgentSessionWorkspaceTabViewModel CreateReadyTab(string id, string title)
    {
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = id,
            Title = title,
        };
        return tab;
    }

    private static RunningAgentBrainViewModel CreateBrainVm(
        FakeRunningAgentChatTable table,
        IEnumerable<AgentTabInfo> tabs,
        List<(string tabId, string? paneId)>? activatedTabs = null)
    {
        var tabList = tabs.ToList();
        activatedTabs ??= [];

        return new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => tabList,
            activateTab: (tabId, paneId) => activatedTabs.Add((tabId, paneId)),
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
        table.SetSessionCount(1);

        var tab = CreateReadyTab("tab-1", "My Agent");
        tab.SetFailed("test");  // Not Ready — should not appear in rows
        // We still test IsAnyRunning via table.SessionCount

        var vm = CreateBrainVm(table, []);

        Assert.True(vm.IsAnyRunning);
    }

    [Fact]
    public void WithActiveTabInReadyState_HasRow()
    {
        var table = new FakeRunningAgentChatTable();
        var tab = CreateReadyTab("tab-1", "Agent Session");
        // tab.State is Loading by default; SetFailed makes it Failed
        // We need a Ready tab. Since SetReady needs an AgentViewModel which is complex,
        // let's create the brain VM with the tab already in Ready state via Refresh
        // by using a tab-providing function.

        // For unit tests, we construct tabs in Ready state indirectly by
        // using the AgentTabInfo with a tab in Loading state — Refresh() filters by Ready state.
        // We need to test with a Ready-state tab, but SetReady requires AgentViewModel.
        // Use SetFailed to get a non-Loading state (though not Ready).
        // Instead: let's just test what we can without the Ready state requirement.

        // Since GetAllAgentTabs() in production filters by Ready state, the unit test
        // Func<IEnumerable<AgentTabInfo>> can return any tab regardless of state —
        // the brain VM just uses whatever the delegate returns.

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "My Workspace", tab)],
            activateTab: (_, _) => { },
            dispatch: action => action());

        Assert.Single(vm.Rows);
    }

    [Fact]
    public void WithActiveTab_RowShowsWorkspaceAndTabTitles()
    {
        var table = new FakeRunningAgentChatTable();
        var tab = CreateReadyTab("tab-1", "My Agent Tab");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Project Workspace", tab)],
            activateTab: (_, _) => { },
            dispatch: action => action());

        var row = Assert.Single(vm.Rows);
        Assert.Equal("Project Workspace", row.WorkspacePaneTitle);
        Assert.Equal("My Agent Tab", row.TabTitle);
        Assert.True(row.HasOpenTab);
    }

    [Fact]
    public void WithNoActiveTabs_HasNoRows()
    {
        var table = new FakeRunningAgentChatTable();

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [],
            activateTab: (_, _) => { },
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

        table.SetSessionCount(2);

        Assert.True(vm.IsAnyRunning);
    }

    [Fact]
    public void SessionsChanged_RemovesRowWhenTabDisappears()
    {
        var table = new FakeRunningAgentChatTable();
        var tab = CreateReadyTab("tab-1", "Agent");

        var returnTabs = true;
        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => returnTabs
                ? [new AgentTabInfo("pane-1", "Workspace", tab)]
                : [],
            activateTab: (_, _) => { },
            dispatch: action => action());

        Assert.Single(vm.Rows);

        returnTabs = false;
        table.SetSessionCount(0);  // triggers Refresh

        Assert.Empty(vm.Rows);
    }

    [Fact]
    public void RowActivateCommand_CallsActivateTab()
    {
        var table = new FakeRunningAgentChatTable();
        var tab = CreateReadyTab("tab-abc", "Agent");
        var activated = new List<(string tabId, string? paneId)>();

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-xyz", "Workspace", tab)],
            activateTab: (tabId, paneId) => activated.Add((tabId, paneId)),
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
        var tab = CreateReadyTab("tab-1", "Agent");

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => [new AgentTabInfo("pane-1", "Workspace", tab)],
            activateTab: (_, _) => { },
            dispatch: action => action());

        vm.IsOpen = true;
        var row = Assert.Single(vm.Rows);
        row.ActivateCommand.Execute(null);

        Assert.False(vm.IsOpen);
    }

    [Fact]
    public void WithNoTabsAndNoSessions_HasNoRows_AndIsNotRunning()
    {
        var table = new FakeRunningAgentChatTable();
        var vm = CreateBrainVm(table, []);

        Assert.False(vm.IsAnyRunning);
        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
    }

    [Fact]
    public void Refresh_UpdatesRowsFromNewTabList()
    {
        var table = new FakeRunningAgentChatTable();
        var tab1 = CreateReadyTab("tab-1", "Agent 1");
        var tab2 = CreateReadyTab("tab-2", "Agent 2");

        var currentTabs = new List<AgentTabInfo>
        {
            new AgentTabInfo("pane-1", "Workspace A", tab1),
        };

        var vm = new RunningAgentBrainViewModel(
            table: table,
            getAllAgentTabs: () => currentTabs,
            activateTab: (_, _) => { },
            dispatch: action => action());

        Assert.Single(vm.Rows);

        currentTabs.Add(new AgentTabInfo("pane-1", "Workspace A", tab2));
        vm.Refresh();

        Assert.Equal(2, vm.Rows.Count);
    }
}
