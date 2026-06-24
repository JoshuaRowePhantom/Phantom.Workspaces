using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

/// <summary>An in-memory <see cref="INotifier"/> that records notifications for assertions.</summary>
public sealed class FakeNotifier : INotifier
{
    public List<Notification> Notifications { get; } = new();

    public void Notify(Notification notification) => this.Notifications.Add(notification);
}
