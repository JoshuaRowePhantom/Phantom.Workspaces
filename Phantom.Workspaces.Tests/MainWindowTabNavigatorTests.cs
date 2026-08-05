using System.Collections.Generic;
using System.Threading.Tasks;
using Phantom.Workspaces.Services.Navigation;
using Phantom.Workspaces.Services.Notifications;
using Xunit;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Unit coverage for <see cref="MainWindowTabNavigator"/> — the single navigation service shared by
/// the brain button, notifications dropdown, and Ctrl nav-stack popup (issue #1254). Orchestration is
/// verified against fakes for the host, navigation-history service, and notification service.
/// </summary>
public sealed class MainWindowTabNavigatorTests
{
    private sealed class FakeHost : ITabNavigatorHost
    {
        public List<(string TabId, string? PaneId)> Activated { get; } = [];
        public List<string> OpenedSessions { get; } = [];
        public int FocusCount { get; private set; }
        public string? SelectedWorkspacePaneId { get; set; }
        public bool NavigatingViaHistory { get; set; }

        public Task ActivateTabByIdAsync(string tabId, string? workspacePaneId)
        {
            this.Activated.Add((tabId, workspacePaneId));
            return Task.CompletedTask;
        }

        public Task OpenAgentForSessionAsync(string sessionKey)
        {
            this.OpenedSessions.Add(sessionKey);
            return Task.CompletedTask;
        }

        public void FocusMainWindow() => this.FocusCount++;
    }

    private sealed class FakeHistory : INavigationHistoryService
    {
        public List<NavigationEntry> Pushed { get; } = [];
        public void Push(NavigationEntry entry) => this.Pushed.Add(entry);
        public bool GoBack(out NavigationEntry? entry) { entry = null; return false; }
        public bool GoForward(out NavigationEntry? entry) { entry = null; return false; }
        public bool GoBackSkipping(System.Func<NavigationEntry, bool> isEntryAvailable, out NavigationEntry? entry) { entry = null; return false; }
        public bool GoForwardSkipping(System.Func<NavigationEntry, bool> isEntryAvailable, out NavigationEntry? entry) { entry = null; return false; }
        public bool GoToIndex(int index, out NavigationEntry? entry) { entry = null; return false; }
        public bool CanGoBack => false;
        public bool CanGoForward => false;
        public IReadOnlyList<NavigationEntry> Entries => this.Pushed;
        public int CurrentIndex => -1;
        public event System.EventHandler? CanNavigateChanged { add { } remove { } }
    }

    private sealed class FakeNotifications : INotificationService
    {
        public List<string> MarkedRead { get; } = [];
        public void Notify(Notification notification) { }
        public void Remove(string tabId) { }
        public void MarkRead(string tabId) => this.MarkedRead.Add(tabId);
        public IReadOnlyList<NotificationEntry> Notifications => [];
        public bool HasActiveRun => false;
        public event System.EventHandler? NotificationsChanged { add { } remove { } }
    }

    private static (MainWindowTabNavigator Navigator, FakeHost Host, FakeHistory History, FakeNotifications Notifications) Create()
    {
        var host = new FakeHost();
        var history = new FakeHistory();
        var notifications = new FakeNotifications();
        return (new MainWindowTabNavigator(host, history, notifications), host, history, notifications);
    }

    [Fact]
    public async Task NavigateAsync_WithTabId_ActivatesTabViaHost()
    {
        var (navigator, host, _, _) = Create();

        var result = await navigator.NavigateAsync(
            new NavigationTarget { TabId = "tab-1", WorkspacePaneId = "pane-1" });

        Assert.True(result);
        var call = Assert.Single(host.Activated);
        Assert.Equal("tab-1", call.TabId);
        Assert.Equal("pane-1", call.PaneId);
    }

    [Fact]
    public async Task NavigateAsync_WithMarkNotificationRead_MarksTargetTabRead()
    {
        var (navigator, _, _, notifications) = Create();

        await navigator.NavigateAsync(
            new NavigationTarget { TabId = "tab-1" },
            new NavigationOptions { MarkNotificationRead = true });

        Assert.Equal(new[] { "tab-1" }, notifications.MarkedRead);
    }

    [Fact]
    public async Task NavigateAsync_WithMarkNotificationReadFalse_DoesNotMark()
    {
        var (navigator, _, _, notifications) = Create();

        await navigator.NavigateAsync(
            new NavigationTarget { TabId = "tab-1" },
            new NavigationOptions { MarkNotificationRead = false });

        Assert.Empty(notifications.MarkedRead);
    }

    [Fact]
    public async Task NavigateAsync_WithPushHistoryTrue_PushesEntryOnce()
    {
        var (navigator, _, history, _) = Create();

        await navigator.NavigateAsync(
            new NavigationTarget { TabId = "tab-1", WorkspacePaneId = "pane-1" },
            new NavigationOptions { PushHistory = true });

        var entry = Assert.Single(history.Pushed);
        Assert.Equal("tab-1", entry.TabId);
        Assert.Equal("pane-1", entry.WorkspacePaneId);
    }

    [Fact]
    public async Task NavigateAsync_WithPushHistory_FallsBackToSelectedPaneWhenTargetPaneNull()
    {
        var (navigator, host, history, _) = Create();
        host.SelectedWorkspacePaneId = "selected-pane";

        await navigator.NavigateAsync(
            new NavigationTarget { TabId = "tab-1" },
            new NavigationOptions { PushHistory = true });

        var entry = Assert.Single(history.Pushed);
        Assert.Equal("selected-pane", entry.WorkspacePaneId);
    }

    [Fact]
    public async Task NavigateAsync_WithPushHistoryFalse_DoesNotPushHistoryEntry()
    {
        var (navigator, _, history, _) = Create();

        await navigator.NavigateAsync(
            new NavigationTarget { TabId = "tab-1", WorkspacePaneId = "pane-1" },
            new NavigationOptions { PushHistory = false });

        Assert.Empty(history.Pushed);
    }

    [Fact]
    public async Task NavigateAsync_WhenNavigatingViaHistory_DoesNotPushEvenIfRequested()
    {
        var (navigator, host, history, _) = Create();
        host.NavigatingViaHistory = true;

        await navigator.NavigateAsync(
            new NavigationTarget { TabId = "tab-1", WorkspacePaneId = "pane-1" },
            new NavigationOptions { PushHistory = true });

        Assert.Empty(history.Pushed);
    }

    [Fact]
    public async Task NavigateAsync_WithFocusWindow_FocusesMainWindow()
    {
        var (navigator, host, _, _) = Create();

        await navigator.NavigateAsync(
            new NavigationTarget { TabId = "tab-1" },
            new NavigationOptions { FocusWindow = true });

        Assert.Equal(1, host.FocusCount);
    }

    [Fact]
    public async Task NavigateAsync_WithoutFocusWindow_DoesNotFocus()
    {
        var (navigator, host, _, _) = Create();

        await navigator.NavigateAsync(
            new NavigationTarget { TabId = "tab-1" },
            new NavigationOptions { FocusWindow = false });

        Assert.Equal(0, host.FocusCount);
    }

    [Fact]
    public async Task NavigateAsync_WithOpenEntityIfNoTab_OpensAgentSessionEntity()
    {
        var (navigator, host, _, _) = Create();

        var result = await navigator.NavigateAsync(
            new NavigationTarget { AgentSessionKey = "session-1" },
            new NavigationOptions { OpenEntityIfNoTab = true });

        Assert.True(result);
        Assert.Equal(new[] { "session-1" }, host.OpenedSessions);
        Assert.Empty(host.Activated);
    }

    [Fact]
    public async Task NavigateAsync_WithNoTabAndNoFallback_ReturnsFalse()
    {
        var (navigator, host, history, notifications) = Create();

        var result = await navigator.NavigateAsync(
            new NavigationTarget { AgentSessionKey = "session-1" },
            new NavigationOptions { OpenEntityIfNoTab = false });

        Assert.False(result);
        Assert.Empty(host.OpenedSessions);
        Assert.Empty(host.Activated);
        Assert.Empty(history.Pushed);
        Assert.Empty(notifications.MarkedRead);
    }

    [Fact]
    public async Task NavigateAsync_WithDefaultOptions_PushesHistoryAndMarksRead()
    {
        var (navigator, host, history, notifications) = Create();
        host.SelectedWorkspacePaneId = "pane-1";

        await navigator.NavigateAsync(new NavigationTarget { TabId = "tab-1" });

        Assert.Single(history.Pushed);
        Assert.Equal(new[] { "tab-1" }, notifications.MarkedRead);
    }
}
