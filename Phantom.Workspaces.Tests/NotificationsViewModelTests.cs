using System;
using System.Collections.Generic;
using System.Linq;
using Phantom.Workspaces.Services.Notifications;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class NotificationsViewModelTests
{
    private sealed class FakeActiveTabProvider : IActiveTabProvider
    {
        public string? ActiveTabId { get; set; }
    }

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
        var viewModel = new NotificationsViewModel(service, _ => { });

        Assert.False(viewModel.HasRows);
    }

    [Fact]
    public void HasRows_WithRows_ReturnsTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });
        
        service.Notify(InterestingNotification(Tab("tab-1"), "Test notification"));

        Assert.True(viewModel.HasRows);
    }

    [Fact]
    public void HasRows_RaisesPropertyChanged_WhenRowsChange()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });
        
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
        var viewModel = new NotificationsViewModel(service, _ => { });

        service.Notify(RunningNotification(Tab("tab-1")));

        Assert.True(viewModel.HasActiveRun);
    }

    [Fact]
    public void HasActiveRun_WhenNoRunActive_ReturnsFalse()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });

        Assert.False(viewModel.HasActiveRun);
    }

    [Fact]
    public void HasActiveRun_RaisesPropertyChanged_WhenRunStarts()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });

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
        var viewModel = new NotificationsViewModel(service, _ => { });
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
    public void OnNotificationsChanged_WhenInterestingNotificationArrives_SetsIsOpenTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });

        service.Notify(InterestingNotification(Tab("tab-1"), "Test notification"));

        Assert.True(viewModel.IsOpen);
    }

    [Fact]
    public void OnNotificationsChanged_WhenInterestingNotificationArrives_SetsIsAutoClosingTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });

        service.Notify(InterestingNotification(Tab("tab-1"), "Test notification"));

        Assert.True(viewModel.IsAutoClosing);
    }

    [Fact]
    public void OnNotificationsChanged_RaisesPropertyChangedForIsAutoClosing()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });

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
        var viewModel = new NotificationsViewModel(service, _ => { });
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));  // opens it

        viewModel.ToggleOpen();

        Assert.False(viewModel.IsOpen);
    }

    [Fact]
    public void ToggleOpen_SetsIsAutoClosingFalse()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));  // sets IsAutoClosing = true

        viewModel.ToggleOpen();

        Assert.False(viewModel.IsAutoClosing);
    }

    [Fact]
    public void ToggleOpen_WhenAutoClosing_RaisesPropertyChangedForIsAutoClosing()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });
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
        var viewModel = new NotificationsViewModel(service, _ => { });

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
        var viewModel = new NotificationsViewModel(service, _ => { });

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
        var viewModel = new NotificationsViewModel(service, _ => { });

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
        var viewModel = new NotificationsViewModel(service, _ => { });

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
        var viewModel = new NotificationsViewModel(service, _ => { });

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
        var viewModel = new NotificationsViewModel(service, _ => { });
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
        var viewModel = new NotificationsViewModel(service, _ => { });
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
        var viewModel = new NotificationsViewModel(service, _ => { });
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
        var viewModel = new NotificationsViewModel(service, _ => { });
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
        var viewModel = new NotificationsViewModel(service, _ => { });
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
        var viewModel = new NotificationsViewModel(service, _ => { });
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
        var viewModel = new NotificationsViewModel(service, _ => { });

        var exception = Record.Exception(() => viewModel.OpenWithHighlight("tab-1"));

        Assert.Null(exception);
        Assert.True(viewModel.IsOpen);
    }

    [Fact]
    public void OnDismissed_ClearsAllHighlights()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });
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
        var viewModel = new NotificationsViewModel(service, _ => { });
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));

        viewModel.ToggleOpen();

        Assert.True(viewModel.Rows.All(r => !r.IsHighlighted));
    }

    [Fact]
    public void NavigateCommand_WhenAutoClosing_DoesNotCloseImmediately()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));
        // IsOpen=true, IsAutoClosing=true at this point

        viewModel.Rows.Single(r => r.TabKey == "tab-1").NavigateCommand.Execute(null);

        Assert.True(viewModel.IsOpen);
    }

    [Fact]
    public void NavigateCommand_WhenAutoClosing_IsAutoClosingRemainsTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var viewModel = new NotificationsViewModel(service, _ => { });
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));

        viewModel.Rows.Single(r => r.TabKey == "tab-1").NavigateCommand.Execute(null);

        Assert.True(viewModel.IsAutoClosing);
    }

    [Fact]
    public void NavigateCommand_InvokesNavigateCallback()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        string? navigatedTo = null;
        var viewModel = new NotificationsViewModel(service, tabKey => navigatedTo = tabKey);
        service.Notify(InterestingNotification(Tab("tab-1"), "notification"));

        viewModel.Rows.Single(r => r.TabKey == "tab-1").NavigateCommand.Execute(null);

        Assert.Equal("tab-1", navigatedTo);
    }
}