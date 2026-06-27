using System;
using System.Linq;
using Phantom.Workspaces.Services.Notifications;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class NotificationServiceTests
{
    private sealed class FakeActiveTabProvider : IActiveTabProvider
    {
        public string? ActiveTabId { get; set; }
    }

    private static TabDescriptor Tab(string tabId) =>
        new TabDescriptor { TabId = tabId };

    [Fact]
    public void Notify_WhenTabIsInactive_AddsUnreadEntry()
    {
        var provider = new FakeActiveTabProvider { ActiveTabId = "other-tab" };
        var service = new NotificationService(provider);

        service.Notify(Tab("tab-1"), "Something happened");

        var entry = Assert.Single(service.Notifications);
        Assert.Equal("tab-1", entry.TabKey);
        Assert.Equal("Something happened", entry.Reason);
        Assert.False(entry.IsRead);
    }

    [Fact]
    public void Notify_WhenTabIsActive_AddsReadEntry()
    {
        var provider = new FakeActiveTabProvider { ActiveTabId = "tab-1" };
        var service = new NotificationService(provider);

        service.Notify(Tab("tab-1"), "Something happened");

        var entry = Assert.Single(service.Notifications);
        Assert.True(entry.IsRead);
    }

    [Fact]
    public void Notify_WhenTabIsSnoozed_AddsReadEntry()
    {
        var provider = new FakeActiveTabProvider { ActiveTabId = "other-tab" };
        var service = new NotificationService(provider);
        service.SnoozeTab("tab-1");

        service.Notify(Tab("tab-1"), "Something happened");

        var entry = Assert.Single(service.Notifications);
        Assert.True(entry.IsRead);
        Assert.True(entry.IsSnoozed);
    }

    [Fact]
    public void Notify_WithNullReason_RemovesEntry()
    {
        var provider = new FakeActiveTabProvider { ActiveTabId = null };
        var service = new NotificationService(provider);
        service.Notify(Tab("tab-1"), "initial");

        service.Notify(Tab("tab-1"), null);

        Assert.Empty(service.Notifications);
    }

    [Fact]
    public void Notify_ReplacesExistingEntryForSameTab()
    {
        var provider = new FakeActiveTabProvider { ActiveTabId = null };
        var service = new NotificationService(provider);
        service.Notify(Tab("tab-1"), "first");
        service.Notify(Tab("tab-1"), "second");

        var entry = Assert.Single(service.Notifications);
        Assert.Equal("second", entry.Reason);
    }

    [Fact]
    public void MarkRead_MarksEntryRead()
    {
        var provider = new FakeActiveTabProvider { ActiveTabId = null };
        var service = new NotificationService(provider);
        service.Notify(Tab("tab-1"), "msg");
        Assert.False(service.Notifications[0].IsRead);

        service.MarkRead("tab-1");

        Assert.True(service.Notifications[0].IsRead);
    }

    [Fact]
    public void SnoozeTab_FutureNotificationsAutoRead()
    {
        var provider = new FakeActiveTabProvider { ActiveTabId = null };
        var service = new NotificationService(provider);
        service.SnoozeTab("tab-1");

        service.Notify(Tab("tab-1"), "msg");

        var entry = Assert.Single(service.Notifications);
        Assert.True(entry.IsRead);
        Assert.True(entry.IsSnoozed);
    }

    [Fact]
    public void NotifyRunning_WhenRunActive_HasActiveRunIsTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);

        service.NotifyRunning("tab-1", true);

        Assert.True(service.HasActiveRun);
    }

    [Fact]
    public void NotifyRunning_WhenAllRunsStop_HasActiveRunIsFalse()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        service.NotifyRunning("tab-1", true);

        service.NotifyRunning("tab-1", false);

        Assert.False(service.HasActiveRun);
    }

    [Fact]
    public void NotifyRunning_WithTwoTabsRunning_HasActiveRunRemainsTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        service.NotifyRunning("tab-1", true);
        service.NotifyRunning("tab-2", true);

        service.NotifyRunning("tab-1", false);

        Assert.True(service.HasActiveRun);
    }

    [Fact]
    public void NotifyRunning_WhenRunActive_FiresNotificationsChanged()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var fired = false;
        service.NotificationsChanged += (_, _) => fired = true;

        service.NotifyRunning("tab-1", true);

        Assert.True(fired);
    }

    [Fact]
    public void NotifyRunning_WhenRunStops_FiresNotificationsChanged()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        service.NotifyRunning("tab-1", true);
        var fired = false;
        service.NotificationsChanged += (_, _) => fired = true;

        service.NotifyRunning("tab-1", false);

        Assert.True(fired);
    }
}
