using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.Updates;

namespace Phantom.Workspaces.ViewModels.Configuration;

/// <summary>
/// The settings section for application updates. It surfaces the running version, the latest known
/// version, the automatic-update mode, and the run-at-startup toggle, and lets the user check for
/// updates or install one on demand. Background availability changes are marshalled through the
/// injected <c>dispatch</c> delegate so the bound state mutates on the UI thread.
/// </summary>
public sealed class UpdateSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly IUpdateController controller;
    private readonly Action<Action> dispatch;
    private readonly RelayCommand checkForUpdatesNowCommand;
    private readonly RelayCommand installUpdateNowCommand;
    private UpdateModeOption selectedMode;
    private bool runAtStartup;
    private bool isBusy;
    private bool isUpdateAvailable;
    private string? latestVersion;
    private string statusText = string.Empty;
    private bool disposed;

    /// <summary>Creates the view model over <paramref name="controller"/> and persisted settings.</summary>
    public UpdateSettingsViewModel(IUpdateController controller, UpdateSettings settings, Action<Action>? dispatch = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(settings);

        this.controller = controller;
        this.dispatch = dispatch ?? (action => action());

        this.Modes =
        [
            new UpdateModeOption(AutomaticUpdateMode.Off, "Off"),
            new UpdateModeOption(AutomaticUpdateMode.NotifyOnly, "Notify only"),
            new UpdateModeOption(AutomaticUpdateMode.DownloadAndInstall, "Download and install automatically"),
        ];
        this.selectedMode = this.Modes.First(option => option.Mode == settings.Mode);
        this.runAtStartup = controller.IsRunAtStartupEnabled;
        this.latestVersion = controller.LatestAvailableVersion;
        this.isUpdateAvailable = controller.LatestAvailableVersion is not null;

        this.checkForUpdatesNowCommand = new RelayCommand(
            _ => _ = this.CheckForUpdatesAsync(),
            _ => !this.isBusy);
        this.installUpdateNowCommand = new RelayCommand(
            _ => _ = this.InstallUpdateAsync(),
            _ => this.isUpdateAvailable && !this.isBusy);

        this.controller.UpdateAvailabilityChanged += this.OnUpdateAvailabilityChanged;
        this.UpdateStatusText();
    }

    /// <summary>The selectable automatic-update modes with display labels.</summary>
    public IReadOnlyList<UpdateModeOption> Modes { get; }

    /// <summary>The running application version.</summary>
    public string RunningVersion => this.controller.RunningVersion;

    /// <summary>The currently selected automatic-update mode option.</summary>
    public UpdateModeOption SelectedMode
    {
        get => this.selectedMode;
        set
        {
            if (value is null || !this.SetProperty(ref this.selectedMode, value))
            {
                return;
            }

            this.controller.Mode = value.Mode;
        }
    }

    /// <summary>Whether the application is registered to run at logon.</summary>
    public bool RunAtStartup
    {
        get => this.runAtStartup;
        set
        {
            if (!this.SetProperty(ref this.runAtStartup, value))
            {
                return;
            }

            this.controller.SetRunAtStartup(value);
        }
    }

    /// <summary>The latest known available version, or <c>null</c> when none/unknown.</summary>
    public string? LatestVersion
    {
        get => this.latestVersion;
        private set => this.SetProperty(ref this.latestVersion, value);
    }

    /// <summary>Whether a newer version is available to install.</summary>
    public bool IsUpdateAvailable
    {
        get => this.isUpdateAvailable;
        private set
        {
            if (this.SetProperty(ref this.isUpdateAvailable, value))
            {
                this.installUpdateNowCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether a check or install is currently running.</summary>
    public bool IsBusy
    {
        get => this.isBusy;
        private set
        {
            if (this.SetProperty(ref this.isBusy, value))
            {
                this.checkForUpdatesNowCommand.RaiseCanExecuteChanged();
                this.installUpdateNowCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>A short human-readable status line.</summary>
    public string StatusText
    {
        get => this.statusText;
        private set => this.SetProperty(ref this.statusText, value);
    }

    /// <summary>Checks the release feed for a newer version.</summary>
    public System.Windows.Input.ICommand CheckForUpdatesNowCommand => this.checkForUpdatesNowCommand;

    /// <summary>Downloads, installs, and relaunches into the latest version.</summary>
    public System.Windows.Input.ICommand InstallUpdateNowCommand => this.installUpdateNowCommand;

    /// <summary>Projects the current selections back to a persistable <see cref="UpdateSettings"/>.</summary>
    public UpdateSettings ToSettings(UpdateSettings current)
        => current with
        {
            Mode = this.selectedMode.Mode,
            RunAtStartup = this.runAtStartup,
        };

    /// <inheritdoc />
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.controller.UpdateAvailabilityChanged -= this.OnUpdateAvailabilityChanged;
    }

    private async Task CheckForUpdatesAsync()
    {
        if (this.IsBusy)
        {
            return;
        }

        this.IsBusy = true;
        this.StatusText = "Checking for updates\u2026";
        try
        {
            var availability = await this.controller.CheckForUpdatesAsync().ConfigureAwait(true);
            this.ApplyAvailability(availability);
        }
        catch (Exception exception)
        {
            this.StatusText = $"Update check failed: {exception.Message}";
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (this.IsBusy || !this.IsUpdateAvailable)
        {
            return;
        }

        this.IsBusy = true;
        this.StatusText = "Downloading and installing update\u2026";
        try
        {
            await this.controller.DownloadInstallAndRelaunchAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            this.StatusText = $"Update install failed: {exception.Message}";
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    private void OnUpdateAvailabilityChanged(object? sender, UpdateAvailability availability)
        => this.dispatch(() => this.ApplyAvailability(availability));

    private void ApplyAvailability(UpdateAvailability availability)
    {
        this.LatestVersion = availability.LatestVersion;
        this.IsUpdateAvailable = availability.IsUpdateAvailable;
        this.UpdateStatusText();
    }

    private void UpdateStatusText()
        => this.StatusText = this.isUpdateAvailable
            ? $"Update available: {this.latestVersion}"
            : $"You are up to date (version {this.controller.RunningVersion}).";
}

/// <summary>An automatic-update mode paired with a display label for selection in the UI.</summary>
public sealed record UpdateModeOption(AutomaticUpdateMode Mode, string Display);
