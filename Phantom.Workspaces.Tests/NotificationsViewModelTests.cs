using System;
using System.Collections.Generic;
using System.Linq;
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class NotificationsViewModelTests
{
    private static TabDescriptor Tab(string tabId) =>
        new TabDescriptor { TabId = tabId };

    private static Notification InterestingNotification(TabDescriptor tab, string description) =>
        new Notification(tab, "Completed", description, DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting);

    private static Notification RunningNotification(TabDescriptor tab) =>
        new Notification(tab, "Running", "doing work", DateTime.UtcNow, RunningState.Running, NotificationState.NotInteresting);

    [Fact]
    public void HasRows_WithNoRows_ReturnsFalse()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        Assert.False(viewModel.HasRows);
    }

    [Fact]
    public void HasRows_WithRows_ReturnsTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        
        service.Notify(InterestingNotification(Tab("tab-1"), "Test notification"));

        Assert.True(viewModel.HasRows);
    }

    [Fact]
    public void HasRows_RaisesPropertyChanged_WhenRowsChange()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        
        bool hasRowsChanged = false;
        viewModel.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(viewModel.HasRows))
            {
                hasRowsChanged = true;
            }
        };

        service.Notify(InterestingNotification(Tab("tab-1"), "Test notification"));

        Assert.True(hasRowsChanged);
        Assert.True(viewModel.HasRows);
    }

    [Fact]
    public void HasActiveRun_WhenServiceHasActiveRun_ReturnsTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        service.Notify(RunningNotification(Tab("tab-1")));

        Assert.True(viewModel.HasActiveRun);
    }

    [Fact]
    public void HasActiveRun_WhenNoRunActive_ReturnsFalse()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        Assert.False(viewModel.HasActiveRun);
    }

    [Fact]
    public void HasActiveRun_RaisesPropertyChanged_WhenRunStarts()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        bool hasActiveRunChanged = false;
        viewModel.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(viewModel.HasActiveRun))
            {
                hasActiveRunChanged = true;
            }
        };

        service.Notify(RunningNotification(Tab("tab-1")));

        Assert.True(hasActiveRunChanged);
    }

    [Fact]
    public void HasActiveRun_RaisesPropertyChanged_WhenRunStops()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        service.Notify(RunningNotification(Tab("tab-1")));

        bool hasActiveRunChanged = false;
        viewModel.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(viewModel.HasActiveRun))
            {
                hasActiveRunChanged = true;
            }
        };

        service.Notify(InterestingNotification(Tab("tab-1"), "done"));

        Assert.True(hasActiveRunChanged);
    }

    [Fact]
    public void OnNotificationsChanged_WhenNotificationMarkedRead_ClearsRowAttentionIndicator()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        service.Notify(InterestingNotification(Tab("tab-1"), "Test notification"));

        var row = viewModel.Rows.Single(r => r.TabKey == "tab-1");
        Assert.True(row.ShowsAttentionIndicator);

        service.MarkRead("tab-1");

        Assert.True(row.IsRead);
        Assert.False(row.ShowsAttentionIndicator);
    }

    [Fact]
    public void OnNotificationsChanged_WhenInterestingNotificationArrives_SetsIsOpenTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        service.Notify(InterestingNotification(Tab("tab-1"), "Test notification"));

        Assert.True(viewModel.IsOpen);
    }

    [Fact]
    public void OnNotificationsChanged_WhenInterestingNotificationArrives_SetsIsAutoClosingTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        service.Notify(InterestingNotification(Tab("tab-1"), "Test notification"));

        Assert.True(viewModel.IsAutoClosing);
    }

    [Fact]
    public void OnNotificationsChanged_RaisesPropertyChangedForIsAutoClosing()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        service.Notify(InterestingNotification(Tab("tab-1"), "Test notification"));

        Assert.Contains(nameof(viewModel.IsAutoClosing), changedProperties);
    }

    [Fact]
    public void ToggleOpen_WhenCalledWhileOpen_ClosesPopup()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));  // opens it

        viewModel.ToggleOpen();

        Assert.False(viewModel.IsOpen);
    }

    [Fact]
    public void ToggleOpen_SetsIsAutoClosingFalse()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));  // sets IsAutoClosing = true

        viewModel.ToggleOpen();

        Assert.False(viewModel.IsAutoClosing);
    }

    [Fact]
    public void ToggleOpen_WhenAutoClosing_RaisesPropertyChangedForIsAutoClosing()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        viewModel.IsAutoClosing = true;

        var changedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.ToggleOpen();

        Assert.Contains(nameof(viewModel.IsAutoClosing), changedProperties);
    }

    [Fact]
    public void OnNotificationsChanged_WhenNotificationRemoved_DoesNotAutoShow()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));  // unread count goes 0 → 1, auto-shows
        viewModel.IsOpen = false;
        viewModel.IsAutoClosing = false;

        service.Remove("tab-1");  // removes the notification, unread count goes 1 → 0

        Assert.False(viewModel.IsOpen);
        Assert.False(viewModel.IsAutoClosing);
    }

    [Fact]
    public void OnNotificationsChanged_WhenNotificationMarkedRead_DoesNotAutoShow()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));  // unread count goes 0 → 1, auto-shows
        viewModel.IsOpen = false;
        viewModel.IsAutoClosing = false;

        service.MarkRead("tab-1");  // unread count goes 1 → 0

        Assert.False(viewModel.IsOpen);
        Assert.False(viewModel.IsAutoClosing);
    }

    [Fact]
    public void OnNotificationsChanged_WhenSecondNotificationAddedAfterFirstRead_AutoShows()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        service.Notify(InterestingNotification(Tab("tab-1"), "first"));   // unread 0 → 1
        service.MarkRead("tab-1");               // unread 1 → 0
        viewModel.IsOpen = false;
        viewModel.IsAutoClosing = false;

        service.Notify(InterestingNotification(Tab("tab-2"), "second"));  // unread 0 → 1 — should auto-show again

        Assert.True(viewModel.IsOpen);
        Assert.True(viewModel.IsAutoClosing);
    }

    [Fact]
    public void Notify_WithNotInterestingState_DoesNotAutoShow()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        // NotInteresting (running state update) should not auto-show popup
        service.Notify(RunningNotification(Tab("tab-1")));

        Assert.False(viewModel.IsOpen);
        Assert.False(viewModel.IsAutoClosing);
    }

    [Fact]
    public void Notify_WithInterestingState_OpensPopup()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        service.Notify(InterestingNotification(Tab("tab-1"), "Test notification"));

        Assert.True(viewModel.IsOpen);
        Assert.True(viewModel.IsAutoClosing);
    }

    // ── OpenWithHighlight tests ────────────────────────────────────────────

    [Fact]
    public void OpenWithHighlight_SetsIsOpenTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));
        viewModel.IsOpen = false;
        viewModel.IsAutoClosing = false;

        viewModel.OpenWithHighlight("tab-1");

        Assert.True(viewModel.IsOpen);
    }

    [Fact]
    public void OpenWithHighlight_SetsIsAutoClosingTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));
        viewModel.IsOpen = false;
        viewModel.IsAutoClosing = false;

        viewModel.OpenWithHighlight("tab-1");

        Assert.True(viewModel.IsAutoClosing);
    }

    [Fact]
    public void OpenWithHighlight_HighlightsTargetRow()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));

        viewModel.OpenWithHighlight("tab-1");

        var row = viewModel.Rows.Single(r => r.TabKey == "tab-1");
        Assert.True(row.IsHighlighted);
    }

    [Fact]
    public void OpenWithHighlight_ClearsOtherRowHighlights()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        service.Notify(InterestingNotification(Tab("tab-1"), "first"));
        service.Notify(InterestingNotification(Tab("tab-2"), "second"));

        viewModel.OpenWithHighlight("tab-1");

        var otherRow = viewModel.Rows.Single(r => r.TabKey == "tab-2");
        Assert.False(otherRow.IsHighlighted);
    }

    [Fact]
    public void OpenWithHighlight_WhenCalledAgain_MovesHighlight()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        service.Notify(InterestingNotification(Tab("tab-1"), "first"));
        service.Notify(InterestingNotification(Tab("tab-2"), "second"));

        viewModel.OpenWithHighlight("tab-1");
        viewModel.OpenWithHighlight("tab-2");

        var row1 = viewModel.Rows.Single(r => r.TabKey == "tab-1");
        var row2 = viewModel.Rows.Single(r => r.TabKey == "tab-2");
        Assert.False(row1.IsHighlighted);
        Assert.True(row2.IsHighlighted);
    }

    [Fact]
    public void OpenWithHighlight_WhenTabKeyNotFound_PopupStillOpens()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));
        viewModel.IsOpen = false;
        viewModel.IsAutoClosing = false;

        viewModel.OpenWithHighlight("tab-not-found");

        Assert.True(viewModel.IsOpen);
        Assert.True(viewModel.IsAutoClosing);
        Assert.True(viewModel.Rows.All(r => !r.IsHighlighted));
    }

    [Fact]
    public void OpenWithHighlight_WhenNoRows_DoesNotThrow()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        var exception = Record.Exception(() => viewModel.OpenWithHighlight("tab-1"));

        Assert.Null(exception);
        Assert.True(viewModel.IsOpen);
    }

    [Fact]
    public void OnDismissed_ClearsAllHighlights()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));
        viewModel.OpenWithHighlight("tab-1");

        viewModel.Dismiss();

        Assert.True(viewModel.Rows.All(r => !r.IsHighlighted));
    }

    [Fact]
    public void ToggleOpen_ManualOpen_NoRowIsHighlighted()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));

        viewModel.ToggleOpen();

        Assert.True(viewModel.Rows.All(r => !r.IsHighlighted));
    }

    // ── Frozen-order tests ────────────────────────────────────────────────

    private static Notification InterestingNotification(TabDescriptor tab, string description, DateTime when) =>
        new Notification(tab, "Completed", description, when, RunningState.Idle, NotificationState.Interesting);

    [Fact]
    public void RefreshRows_WhenOpen_DoesNotReorderExistingRows()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        var baseTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        // tab-2 is older (added first); popup opens. tab-1 is newer; popup already open so tab-1 is prepended.
        service.Notify(InterestingNotification(Tab("tab-2"), "older", baseTime));
        service.Notify(InterestingNotification(Tab("tab-1"), "newer", baseTime.AddSeconds(1)));

        // Both unread; tab-1 (newer) should be at index 0
        Assert.Equal("tab-1", viewModel.Rows[0].TabKey);
        Assert.Equal("tab-2", viewModel.Rows[1].TabKey);

        // Mark tab-1 as read while the popup is still open
        service.MarkRead("tab-1");

        // Order must not change — tab-1 stays at index 0
        Assert.Equal("tab-1", viewModel.Rows[0].TabKey);
        Assert.Equal("tab-2", viewModel.Rows[1].TabKey);
    }

    [Fact]
    public void RefreshRows_WhenOpen_AppendsNewNotificationAtTop()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        service.Notify(InterestingNotification(Tab("tab-1"), "first"));
        // Popup is now open with tab-1 at index 0
        Assert.True(viewModel.IsOpen);
        Assert.Equal("tab-1", viewModel.Rows[0].TabKey);

        // Notify a second tab while popup is open
        service.Notify(InterestingNotification(Tab("tab-2"), "second"));

        // New notification should appear at index 0; existing row stays at index 1
        Assert.Equal("tab-2", viewModel.Rows[0].TabKey);
        Assert.Equal("tab-1", viewModel.Rows[1].TabKey);
    }

    [Fact]
    public void RefreshRows_AfterClose_ResortsByReadThenTime()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());

        var baseTime = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        // tab-2 older, tab-1 newer; both unread; tab-1 ends up at index 0 after both are notified
        service.Notify(InterestingNotification(Tab("tab-2"), "older", baseTime));
        service.Notify(InterestingNotification(Tab("tab-1"), "newer", baseTime.AddSeconds(1)));
        Assert.Equal("tab-1", viewModel.Rows[0].TabKey);

        // Mark tab-1 as read while the popup is open; order frozen — tab-1 stays at 0
        service.MarkRead("tab-1");
        Assert.Equal("tab-1", viewModel.Rows[0].TabKey);

        // Close the popup — rows should now be re-sorted (unread first)
        viewModel.Dismiss();

        Assert.Equal("tab-2", viewModel.Rows[0].TabKey);
        Assert.Equal("tab-1", viewModel.Rows[1].TabKey);
    }

    // ── NavigateCommand tests ──────────────────────────────────────────────

    [Fact]
    public void NavigateCommand_WhenAutoClosing_DoesNotCloseImmediately()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));
        Assert.True(viewModel.IsOpen);
        Assert.True(viewModel.IsAutoClosing);

        var row = viewModel.Rows.Single(r => r.TabKey == "tab-1");
        row.NavigateCommand.Execute(null);

        Assert.True(viewModel.IsOpen);
    }

    [Fact]
    public void NavigateCommand_WhenAutoClosing_IsAutoClosingRemainsTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, new FakeTabNavigator());
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));
        Assert.True(viewModel.IsAutoClosing);

        var row = viewModel.Rows.Single(r => r.TabKey == "tab-1");
        row.NavigateCommand.Execute(null);

        Assert.True(viewModel.IsAutoClosing);
    }

    [Fact]
    public void NavigateCommand_DelegatesToTabNavigator()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var navigator = new FakeTabNavigator();
        var viewModel = new NotificationsViewModel(service, navigator);
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));

        var row = viewModel.Rows.Single(r => r.TabKey == "tab-1");
        row.NavigateCommand.Execute(null);

        var call = Assert.Single(navigator.Calls);
        Assert.Equal("tab-1", call.Target.TabId);
        Assert.True(call.Options.PushHistory);
        Assert.True(call.Options.FocusWindow);
    }

    [Fact]
    public void NavigateCommand_ResolvesWorkspacePaneIdFromNotificationDescriptor()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var navigator = new FakeTabNavigator();
        var viewModel = new NotificationsViewModel(service, navigator);
        service.Notify(new Notification(
            new TabDescriptor { TabId = "tab-1", WorkspaceId = "pane-9" },
            "Completed",
            "done",
            DateTime.UtcNow,
            RunningState.Idle,
            NotificationState.Interesting));

        var row = viewModel.Rows.Single(r => r.TabKey == "tab-1");
        row.NavigateCommand.Execute(null);

        var call = Assert.Single(navigator.Calls);
        Assert.Equal("pane-9", call.Target.WorkspacePaneId);
    }

    [Fact]
    public void NavigateCommand_TriggersFadeCloseBeforeNavigating()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var navigator = new FakeTabNavigator();
        var viewModel = new NotificationsViewModel(service, navigator);
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));

        var events = new List<string>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(viewModel.IsAutoClosing))
            {
                events.Add("fade");
            }
        };

        var row = viewModel.Rows.Single(r => r.TabKey == "tab-1");
        row.NavigateCommand.Execute(null);

        // TriggerFadeClose (which raises IsAutoClosing) runs before the navigator is invoked.
        Assert.Contains("fade", events);
        Assert.Single(navigator.Calls);
    }
}
