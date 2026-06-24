using System;
using Avalonia.Controls;
using Phantom.Workspaces.Install;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Services.Updates;

/// <summary>
/// Owns the Windows notification-area (tray) icon and its <see cref="NativeMenu"/>, wiring its
/// commands to the shared <see cref="IUpdateController"/> and to the supplied open/settings/exit
/// callbacks. It subscribes to <see cref="IUpdateController.UpdateAvailabilityChanged"/> to keep the
/// "Update now" item, the status line, the tray tooltip/icon badge, and a toast in sync. All UI
/// mutations from the (possibly background) availability event are marshalled through the injected
/// <c>dispatch</c> delegate so they run on the UI thread.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly IUpdateController updateController;
    private readonly INotifier notifier;
    private readonly Action<Action> dispatch;
    private readonly TrayIcon trayIcon;
    private readonly NativeMenuItem statusItem;
    private readonly NativeMenuItem updateNowItem;
    private readonly NativeMenuItem runAtStartupItem;
    private readonly NativeMenuItem checkForUpdatesItem;
    private bool disposed;

    /// <summary>Creates the controller and shows the tray icon.</summary>
    public TrayIconController(
        IUpdateController updateController,
        Action openWindow,
        Action openSettings,
        Action exit,
        INotifier? notifier = null,
        Action<Action>? dispatch = null)
    {
        ArgumentNullException.ThrowIfNull(updateController);
        ArgumentNullException.ThrowIfNull(openWindow);
        ArgumentNullException.ThrowIfNull(openSettings);
        ArgumentNullException.ThrowIfNull(exit);

        this.updateController = updateController;
        this.notifier = notifier ?? NullNotifier.Instance;
        this.dispatch = dispatch ?? (action => action());

        var openItem = new NativeMenuItem("Open Phantom.Workspaces")
        {
            Command = new RelayCommand(_ => openWindow()),
        };

        this.statusItem = new NativeMenuItem { IsEnabled = false };

        var checkItem = new NativeMenuItem("Check for updates")
        {
            Command = new RelayCommand(_ => _ = this.updateController.CheckForUpdatesAsync()),
        };
        this.checkForUpdatesItem = checkItem;

        this.updateNowItem = new NativeMenuItem("Update now")
        {
            IsVisible = false,
            Command = new RelayCommand(_ => _ = this.updateController.DownloadInstallAndRelaunchAsync()),
        };

        this.runAtStartupItem = new NativeMenuItem("Run at startup")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = this.updateController.IsRunAtStartupEnabled,
        };
        this.runAtStartupItem.Command = new RelayCommand(_ =>
        {
            this.updateController.SetRunAtStartup(!this.updateController.IsRunAtStartupEnabled);
            this.runAtStartupItem.IsChecked = this.updateController.IsRunAtStartupEnabled;
        });

        var settingsItem = new NativeMenuItem("Settings\u2026")
        {
            Command = new RelayCommand(_ => openSettings()),
        };

        var exitItem = new NativeMenuItem("Exit")
        {
            Command = new RelayCommand(_ => exit()),
        };

        var menu = new NativeMenu
        {
            openItem,
            this.statusItem,
            new NativeMenuItemSeparator(),
            checkItem,
            this.updateNowItem,
            this.runAtStartupItem,
            new NativeMenuItemSeparator(),
            settingsItem,
            new NativeMenuItemSeparator(),
            exitItem,
        };

        this.trayIcon = new TrayIcon
        {
            ToolTipText = "Phantom.Workspaces",
            Menu = menu,
            IsVisible = true,
        };
        this.trayIcon.Clicked += (_, _) => openWindow();

        this.updateController.UpdateAvailabilityChanged += this.OnUpdateAvailabilityChanged;
        this.ApplyAvailability(
            new UpdateAvailability(
                this.updateController.LatestAvailableVersion is not null,
                this.updateController.LatestAvailableVersion),
            notify: false);
    }

    /// <summary>The "Update now" menu item; enabled only when an update is available.</summary>
    public NativeMenuItem UpdateNowItem => this.updateNowItem;

    /// <summary>The checkable "Run at startup" menu item.</summary>
    public NativeMenuItem RunAtStartupItem => this.runAtStartupItem;

    /// <summary>The disabled status line item reflecting current/latest versions.</summary>
    public NativeMenuItem StatusItem => this.statusItem;

    /// <summary>The "Check for updates" menu item.</summary>
    public NativeMenuItem CheckForUpdatesItem => this.checkForUpdatesItem;

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.updateController.UpdateAvailabilityChanged -= this.OnUpdateAvailabilityChanged;
        this.trayIcon.IsVisible = false;
        this.trayIcon.Dispose();
    }

    private void OnUpdateAvailabilityChanged(object? sender, UpdateAvailability availability)
        => this.dispatch(() => this.ApplyAvailability(availability, notify: availability.IsUpdateAvailable));

    private void ApplyAvailability(UpdateAvailability availability, bool notify)
    {
        this.updateNowItem.IsVisible = availability.IsUpdateAvailable;
        this.runAtStartupItem.IsChecked = this.updateController.IsRunAtStartupEnabled;
        this.trayIcon.Icon = TrayIconImageFactory.Create(availability.IsUpdateAvailable);

        if (availability.IsUpdateAvailable)
        {
            this.statusItem.Header = $"Update available: {availability.LatestVersion}";
            this.updateNowItem.Header = $"Update now ({availability.LatestVersion})";
            this.trayIcon.ToolTipText = $"Phantom.Workspaces \u2014 update {availability.LatestVersion} available";

            if (notify)
            {
                this.notifier.Notify(new Notification
                {
                    Title = "Phantom.Workspaces update available",
                    Message = $"Version {availability.LatestVersion} is available. Use the tray menu to update.",
                });
            }
        }
        else
        {
            this.statusItem.Header = $"Up to date (version {this.updateController.RunningVersion})";
            this.updateNowItem.Header = "Update now";
            this.trayIcon.ToolTipText = "Phantom.Workspaces";
        }
    }
}
