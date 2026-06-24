using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class NotifierTests
{
    [Fact]
    public void FakeNotifier_RecordsNotifications()
    {
        var notifier = new FakeNotifier();
        var notification = new Notification { Title = "Update available", Message = "0.2.0 is ready" };

        notifier.Notify(notification);

        Assert.Single(notifier.Notifications);
        Assert.Equal(notification, notifier.Notifications[0]);
        Assert.Equal(NotificationKind.Information, notifier.Notifications[0].Kind);
    }

    [Fact]
    public void NullNotifier_DoesNothing()
    {
        NullNotifier.Instance.Notify(new Notification { Title = "x", Message = "y" });
    }
}
