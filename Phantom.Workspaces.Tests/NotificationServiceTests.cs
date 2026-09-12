using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Phantom.Workspaces.Services.Notifications;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class NotificationServiceTests
{
    private static TabDescriptor Tab(string tabId) =>
        new TabDescriptor { TabId = tabId };

    private static Notification InterestingNotification(TabDescriptor tab, string heading, string description) =>
        new Notification(tab, heading, description, DateTime.UtcNow, RunningState.Idle, NotificationState.Interesting);

    private static Notification RunningNotification(TabDescriptor tab) =>
        new Notification(tab, "Running", "doing work", DateTime.UtcNow, RunningState.Running, NotificationState.Interesting);

    private static Notification SilentNotification(TabDescriptor tab, string description) =>
        new Notification(tab, "Running", description, DateTime.UtcNow, RunningState.Running, NotificationState.NotInteresting);

    [Fact]
    public void Notify_WhenTabIsInactive_AddsUnreadEntry()
    {
        var provider = new FakeActiveTabProvider { ActiveTabId = "other-tab" };
        var service = new NotificationService(provider);

        service.Notify(InterestingNotification(Tab("tab-1"), "Completed", "Something happened"));

        var entry = Assert.Single(service.Notifications);
        Assert.Equal("tab-1", entry.TabKey);
        Assert.Equal("Something happened", entry.Description);
        Assert.False(entry.IsRead);
    }

    [Fact]
    public void Notify_WhenTabIsActive_AddsReadEntry()
    {
        var provider = new FakeActiveTabProvider { ActiveTabId = "tab-1" };
        var service = new NotificationService(provider);

        service.Notify(InterestingNotification(Tab("tab-1"), "Completed", "Something happened"));

        var entry = Assert.Single(service.Notifications);
        Assert.True(entry.IsRead);
    }

    [Fact]
    public void Notify_WhenTabIsSnoozed_AddsReadEntry()
    {
        var provider = new FakeActiveTabProvider { ActiveTabId = "other-tab" };
        var service = new NotificationService(provider);
        service.SnoozeTab("tab-1");

        service.Notify(InterestingNotification(Tab("tab-1"), "Completed", "Something happened"));

        var entry = Assert.Single(service.Notifications);
        Assert.True(entry.IsRead);
        Assert.True(entry.IsSnoozed);
    }

    [Fact]
    public void Remove_RemovesEntry()
    {
        var provider = new FakeActiveTabProvider { ActiveTabId = null };
        var service = new NotificationService(provider);
        service.Notify(InterestingNotification(Tab("tab-1"), "Completed", "initial"));

        service.Remove("tab-1");

        Assert.Empty(service.Notifications);
    }

    [Fact]
    public void Notify_ReplacesExistingEntryForSameTab()
    {
        var provider = new FakeActiveTabProvider { ActiveTabId = null };
        var service = new NotificationService(provider);
        service.Notify(InterestingNotification(Tab("tab-1"), "Completed", "first"));
        service.Notify(InterestingNotification(Tab("tab-1"), "Completed", "second"));

        var entry = Assert.Single(service.Notifications);
        Assert.Equal("second", entry.Description);
    }

    [Fact]
    public void MarkRead_MarksEntryRead()
    {
        var provider = new FakeActiveTabProvider { ActiveTabId = null };
        var service = new NotificationService(provider);
        service.Notify(InterestingNotification(Tab("tab-1"), "Completed", "msg"));
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

        service.Notify(InterestingNotification(Tab("tab-1"), "Completed", "msg"));

        var entry = Assert.Single(service.Notifications);
        Assert.True(entry.IsRead);
        Assert.True(entry.IsSnoozed);
    }

    [Fact]
    public void Notify_WithRunningState_HasActiveRunIsTrue()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);

        service.Notify(RunningNotification(Tab("tab-1")));

        Assert.True(service.HasActiveRun);
    }

    [Fact]
    public void Notify_WithIdleState_HasActiveRunIsFalse()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        service.Notify(RunningNotification(Tab("tab-1")));

        service.Notify(InterestingNotification(Tab("tab-1"), "Completed", "done"));

        Assert.False(service.HasActiveRun);
    }

    [Fact]
    public void HasActiveRun_WithTwoRunningTabs_RemainsTrue_WhenOneGoesIdle()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        service.Notify(RunningNotification(Tab("tab-1")));
        service.Notify(RunningNotification(Tab("tab-2")));

        service.Notify(InterestingNotification(Tab("tab-1"), "Completed", "done"));

        Assert.True(service.HasActiveRun);
    }

    [Fact]
    public void Notify_WithRunningState_FiresNotificationsChanged()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        var fired = false;
        service.NotificationsChanged += (_, _) => fired = true;

        service.Notify(RunningNotification(Tab("tab-1")));

        Assert.True(fired);
    }

    [Fact]
    public void Notify_WithIdleState_FiresNotificationsChanged()
    {
        var provider = new FakeActiveTabProvider();
        var service = new NotificationService(provider);
        service.Notify(RunningNotification(Tab("tab-1")));
        var fired = false;
        service.NotificationsChanged += (_, _) => fired = true;

        service.Notify(InterestingNotification(Tab("tab-1"), "Completed", "done"));

        Assert.True(fired);
    }

    [Fact]
    public void Notify_WithNotInterestingState_PreservesExistingIsRead()
    {
        var provider = new FakeActiveTabProvider { ActiveTabId = "other-tab" };
        var service = new NotificationService(provider);
        // Interesting notification creates entry with IsRead=false
        service.Notify(InterestingNotification(Tab("tab-1"), "Running", "started"));

        // NotInteresting update should preserve IsRead=false
        service.Notify(SilentNotification(Tab("tab-1"), "still running"));

        Assert.False(service.Notifications[0].IsRead);
    }

    [Fact]
    public void Notify_WithNotInterestingState_OnNewEntry_CreatesReadEntry()
    {
        var provider = new FakeActiveTabProvider { ActiveTabId = "other-tab" };
        var service = new NotificationService(provider);

        // First ever notification is NotInteresting — should be marked read silently
        service.Notify(SilentNotification(Tab("tab-1"), "doing stuff"));

        Assert.True(service.Notifications[0].IsRead);
    }

    [Fact]
    public void Notification_PropertyShape_RequiredMembersAndLegacyKindDefault_ArePreserved()
    {
        var required = typeof(Notification).GetProperties()
            .Where(property => property.IsDefined(typeof(RequiredMemberAttribute), inherit: false))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            [
                nameof(Notification.Description),
                nameof(Notification.Heading),
                nameof(Notification.NotificationState),
                nameof(Notification.RunningState),
                nameof(Notification.TabDescriptor),
                nameof(Notification.When),
            ],
            required);
        Assert.Equal("legacy", new Notification
        {
            TabDescriptor = Tab("tab"),
            Heading = "heading",
            Description = "description",
            When = DateTime.UnixEpoch,
            RunningState = RunningState.Idle,
            NotificationState = NotificationState.Interesting,
        }.Kind);
    }

    [Fact]
    public void Notification_NamedInitializer_PreservesAllValues()
    {
        var notification = new Notification
        {
            TabDescriptor = Tab("tab"),
            Heading = "heading",
            Description = "description",
            When = DateTime.UnixEpoch,
            RunningState = RunningState.Running,
            NotificationState = NotificationState.NotInteresting,
            Kind = "modal-pending",
        };

        Assert.Equal("tab", notification.TabDescriptor.TabId);
        Assert.Equal("heading", notification.Heading);
        Assert.Equal("description", notification.Description);
        Assert.Equal(DateTime.UnixEpoch, notification.When);
        Assert.Equal(RunningState.Running, notification.RunningState);
        Assert.Equal(NotificationState.NotInteresting, notification.NotificationState);
        Assert.Equal("modal-pending", notification.Kind);
    }

    [Fact]
    public void Notify_SameTabDifferentKinds_PreservesIndependentEntries()
    {
        var service = new NotificationService(new FakeActiveTabProvider());

        service.Notify(InterestingNotification(Tab("tab"), "idle", "done") with { Kind = "chat-idle" });
        service.Notify(InterestingNotification(Tab("tab"), "modal", "input") with { Kind = "modal-pending" });

        Assert.Equal(2, service.Notifications.Count);
        Assert.Contains(service.Notifications, entry => entry.Kind == "chat-idle" && entry.Description == "done");
        Assert.Contains(service.Notifications, entry => entry.Kind == "modal-pending" && entry.Description == "input");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Notify_BlankKind_ThrowsArgumentException(string kind)
    {
        var service = new NotificationService(new FakeActiveTabProvider());
        Assert.Throws<ArgumentException>(
            () => service.Notify(InterestingNotification(Tab("tab"), "heading", "description") with { Kind = kind }));
    }

    [Fact]
    public void NotificationTargetRequest_NamedInitializer_TargetsOneKind()
    {
        var request = new NotificationTargetRequest { TabId = "tab", Kind = "modal-pending" };
        Assert.Equal("tab", request.TabId);
        Assert.Equal("modal-pending", request.Kind);
    }

    [Fact]
    public void Remove_TabAndKind_RemovesOnlyMatchingKind()
    {
        var service = new NotificationService(new FakeActiveTabProvider());
        service.Notify(InterestingNotification(Tab("tab"), "idle", "done") with { Kind = "chat-idle" });
        service.Notify(InterestingNotification(Tab("tab"), "modal", "input") with { Kind = "modal-pending" });

        service.Remove(new NotificationTargetRequest { TabId = "tab", Kind = "chat-idle" });

        Assert.Collection(service.Notifications, entry => Assert.Equal("modal-pending", entry.Kind));
    }

    [Fact]
    public void MarkRead_TabAndKind_MarksOnlyMatchingKind()
    {
        var service = new NotificationService(new FakeActiveTabProvider());
        service.Notify(InterestingNotification(Tab("tab"), "idle", "done") with { Kind = "chat-idle" });
        service.Notify(InterestingNotification(Tab("tab"), "modal", "input") with { Kind = "modal-pending" });

        service.MarkRead(new NotificationTargetRequest { TabId = "tab", Kind = "chat-idle" });

        Assert.True(service.Notifications.Single(entry => entry.Kind == "chat-idle").IsRead);
        Assert.False(service.Notifications.Single(entry => entry.Kind == "modal-pending").IsRead);
    }
}
