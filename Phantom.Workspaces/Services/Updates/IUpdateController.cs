using System;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Services.Updates;

/// <summary>The result of an update check: whether a newer release exists and its version.</summary>
public sealed record UpdateAvailability(bool IsUpdateAvailable, string? LatestVersion)
{
    /// <summary>A shared "no update available" result.</summary>
    public static UpdateAvailability None { get; } = new(false, null);
}

/// <summary>
/// The GUI-facing facade over the update subsystem, shared by the Updates settings section and the
/// system tray. It exposes the running version, run-at-startup state, on-demand checking, and the
/// download/install/relaunch flow, and raises <see cref="UpdateAvailabilityChanged"/> when a
/// periodic or on-demand check changes availability. Implementations marshal nothing to the UI
/// thread themselves; consumers are responsible for thread affinity.
/// </summary>
public interface IUpdateController
{
    /// <summary>The running informational version (e.g. <c>0.1.0</c>).</summary>
    string RunningVersion { get; }

    /// <summary>The automatic-update mode governing periodic checks and auto-install.</summary>
    Phantom.Workspaces.Configuration.AutomaticUpdateMode Mode { get; set; }

    /// <summary>The latest known available version, or <c>null</c> when none/unknown.</summary>
    string? LatestAvailableVersion { get; }

    /// <summary>Whether the per-user logon scheduled task is currently registered.</summary>
    bool IsRunAtStartupEnabled { get; }

    /// <summary>Raised whenever update availability changes (periodic check or on-demand check).</summary>
    event EventHandler<UpdateAvailability>? UpdateAvailabilityChanged;

    /// <summary>Checks the release feed once and returns the resulting availability.</summary>
    Task<UpdateAvailability> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and stages the latest available release, repoints <c>current</c>, and relaunches
    /// into the new version. Does nothing when no update is available.
    /// </summary>
    Task DownloadInstallAndRelaunchAsync(CancellationToken cancellationToken = default);

    /// <summary>Registers or unregisters the per-user logon scheduled task.</summary>
    void SetRunAtStartup(bool enabled);
}
