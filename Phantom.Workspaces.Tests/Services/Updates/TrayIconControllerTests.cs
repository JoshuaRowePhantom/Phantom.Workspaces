using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Install;
using Phantom.Workspaces.Services.Updates;

namespace Phantom.Workspaces.Tests.Updates;

public sealed class TrayIconControllerTests
{
    [PhantomAvaloniaFact]
    public void Constructor_HidesUpdateNowAndShowsUpToDateStatus()
    {
        var controller = new FakeUpdateController { RunningVersion = "1.0.0" };
        using var tray = CreateController(controller, out _);

        Assert.False(tray.UpdateNowItem.IsVisible);
        Assert.Contains("1.0.0", tray.StatusItem.Header);
    }

    [PhantomAvaloniaFact]
    public void UpdateAvailable_ShowsUpdateNowStatusAndToast()
    {
        var controller = new FakeUpdateController { RunningVersion = "1.0.0" };
        using var tray = CreateController(controller, out var notifier);

        controller.RaiseAvailability(new UpdateAvailability(true, "1.2.0"));

        Assert.True(tray.UpdateNowItem.IsVisible);
        Assert.Contains("1.2.0", tray.StatusItem.Header);
        var notification = Assert.Single(notifier.Notifications);
        Assert.Contains("1.2.0", notification.Message);
    }

    [PhantomAvaloniaFact]
    public void CheckForUpdatesItem_InvokesControllerCheck()
    {
        var controller = new FakeUpdateController();
        using var tray = CreateController(controller, out _);

        tray.CheckForUpdatesItem.Command!.Execute(null);

        Assert.Equal(1, controller.CheckCount);
    }

    [PhantomAvaloniaFact]
    public void UpdateNowItem_InvokesDownloadInstall()
    {
        var controller = new FakeUpdateController();
        using var tray = CreateController(controller, out _);

        tray.UpdateNowItem.Command!.Execute(null);

        Assert.Equal(1, controller.InstallCount);
    }

    [PhantomAvaloniaFact]
    public void RunAtStartupItem_TogglesController()
    {
        var controller = new FakeUpdateController();
        using var tray = CreateController(controller, out _);

        tray.RunAtStartupItem.Command!.Execute(null);
        Assert.True(controller.RunAtStartup);
        Assert.True(tray.RunAtStartupItem.IsChecked);

        tray.RunAtStartupItem.Command!.Execute(null);
        Assert.False(controller.RunAtStartup);
        Assert.False(tray.RunAtStartupItem.IsChecked);
    }

    private static TrayIconController CreateController(FakeUpdateController controller, out RecordingNotifier notifier)
    {
        notifier = new RecordingNotifier();
        return new TrayIconController(
            controller,
            openWindow: () => { },
            openSettings: () => { },
            exit: () => { },
            notifier: notifier,
            dispatch: action => action());
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<Notification> Notifications { get; } = new();

        public void Notify(Notification notification) => this.Notifications.Add(notification);
    }

    private sealed class FakeUpdateController : IUpdateController
    {
        public string RunningVersion { get; set; } = "1.0.0";

        public AutomaticUpdateMode Mode { get; set; } = AutomaticUpdateMode.NotifyOnly;

        public string? LatestAvailableVersion { get; private set; }

        public bool RunAtStartup { get; private set; }

        public bool IsRunAtStartupEnabled => this.RunAtStartup;

        public int CheckCount { get; private set; }

        public int InstallCount { get; private set; }

        public event EventHandler<UpdateAvailability>? UpdateAvailabilityChanged;

        public Task<UpdateAvailability> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
        {
            this.CheckCount++;
            return Task.FromResult(UpdateAvailability.None);
        }

        public Task DownloadInstallAndRelaunchAsync(CancellationToken cancellationToken = default)
        {
            this.InstallCount++;
            return Task.CompletedTask;
        }

        public void SetRunAtStartup(bool enabled) => this.RunAtStartup = enabled;

        public void RaiseAvailability(UpdateAvailability availability)
        {
            this.LatestAvailableVersion = availability.LatestVersion;
            this.UpdateAvailabilityChanged?.Invoke(this, availability);
        }
    }
}
