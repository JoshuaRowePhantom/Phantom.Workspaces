using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Services.Updates;

/// <summary>
/// The production <see cref="IUpdateController"/>. It wraps the <see cref="UpdateService"/>
/// (check/download/stage/apply), the <see cref="StartupTaskService"/> (run-at-startup task), and a
/// clock-driven <see cref="UpdateCheckScheduler"/> for the periodic check. Applying an update
/// stages a fresh version directory and launches that version's executable in
/// <c>--apply-update --relaunch</c> mode, which waits for this process to exit (releasing the
/// single-instance lock), repoints <c>current</c>, and relaunches; this process is then asked to
/// shut down so the swap can proceed.
/// </summary>
public sealed class UpdateController : IUpdateController, IDisposable
{
    private readonly UpdateService updateService;
    private readonly StartupTaskService startupTaskService;
    private readonly InstallLayout layout;
    private readonly IProcessLauncher processLauncher;
    private readonly string? installRootOverride;
    private readonly Action requestShutdown;
    private readonly UpdateCheckScheduler scheduler;
    private readonly object gate = new();
    private ReleaseInfo? latestRelease;
    private AutomaticUpdateMode mode;
    private Timer? periodicTimer;
    private bool disposed;

    /// <summary>Creates the controller over its collaborators.</summary>
    public UpdateController(
        UpdateService updateService,
        StartupTaskService startupTaskService,
        InstallLayout layout,
        IProcessLauncher processLauncher,
        string runningVersion,
        AutomaticUpdateMode mode,
        string? installRootOverride,
        Action requestShutdown,
        UpdateCheckScheduler? scheduler = null)
    {
        ArgumentNullException.ThrowIfNull(updateService);
        ArgumentNullException.ThrowIfNull(startupTaskService);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(processLauncher);
        ArgumentException.ThrowIfNullOrWhiteSpace(runningVersion);
        ArgumentNullException.ThrowIfNull(requestShutdown);

        this.updateService = updateService;
        this.startupTaskService = startupTaskService;
        this.layout = layout;
        this.processLauncher = processLauncher;
        this.RunningVersion = runningVersion;
        this.mode = mode;
        this.installRootOverride = installRootOverride;
        this.requestShutdown = requestShutdown;
        this.scheduler = scheduler ?? new UpdateCheckScheduler(new SystemClock());
    }

    /// <inheritdoc />
    public event EventHandler<UpdateAvailability>? UpdateAvailabilityChanged;

    /// <inheritdoc />
    public string RunningVersion { get; }

    /// <inheritdoc />
    public string? LatestAvailableVersion { get; private set; }

    /// <inheritdoc />
    public bool IsRunAtStartupEnabled => this.startupTaskService.IsEnabled();

    /// <summary>The automatic-update mode governing periodic checks and auto-install.</summary>
    public AutomaticUpdateMode Mode
    {
        get
        {
            lock (this.gate)
            {
                return this.mode;
            }
        }

        set
        {
            lock (this.gate)
            {
                this.mode = value;
            }
        }
    }

    /// <inheritdoc />
    public async Task<UpdateAvailability> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var result = await this.updateService.CheckAsync(cancellationToken).ConfigureAwait(false);
        this.scheduler.MarkChecked();

        ReleaseInfo? release = result.IsUpdateAvailable ? result.LatestRelease : null;
        var availability = new UpdateAvailability(
            result.IsUpdateAvailable,
            release is not null ? NormalizeVersion(release.TagName) : null);

        lock (this.gate)
        {
            this.latestRelease = release;
        }

        this.LatestAvailableVersion = availability.LatestVersion;
        this.UpdateAvailabilityChanged?.Invoke(this, availability);
        return availability;
    }

    /// <inheritdoc />
    public async Task DownloadInstallAndRelaunchAsync(CancellationToken cancellationToken = default)
    {
        ReleaseInfo? release;
        lock (this.gate)
        {
            release = this.latestRelease;
        }

        if (release is null)
        {
            return;
        }

        var version = await this.updateService.DownloadAndStageAsync(release, cancellationToken).ConfigureAwait(false);

        var arguments = new List<string>
        {
            "--apply-update",
            this.layout.GetVersionDirectory(version),
            "--relaunch",
        };
        if (!string.IsNullOrWhiteSpace(this.installRootOverride))
        {
            arguments.Add("--install-root");
            arguments.Add(this.installRootOverride!);
        }

        this.processLauncher.Start(new ProcessStartRequest
        {
            FileName = this.layout.GetVersionExecutablePath(version),
            Arguments = arguments,
        });

        // Releasing the single-instance lock lets the apply-update process repoint current.
        this.requestShutdown();
    }

    /// <inheritdoc />
    public void SetRunAtStartup(bool enabled)
    {
        if (enabled)
        {
            this.startupTaskService.Enable();
        }
        else
        {
            this.startupTaskService.Disable();
        }
    }

    /// <summary>
    /// Starts the periodic background check. The timer drives the clock-based scheduler; a check
    /// runs shortly after startup and then once per scheduler interval. Checks are skipped while the
    /// mode is <see cref="AutomaticUpdateMode.Off"/>, and an available update is applied
    /// automatically when the mode is <see cref="AutomaticUpdateMode.DownloadAndInstall"/>.
    /// </summary>
    public void StartPeriodicChecks(TimeSpan? initialDelay = null, TimeSpan? pollInterval = null)
    {
        lock (this.gate)
        {
            ObjectDisposedException.ThrowIf(this.disposed, this);
            this.periodicTimer?.Dispose();
            this.periodicTimer = new Timer(
                _ => _ = this.PollAsync(),
                state: null,
                initialDelay ?? TimeSpan.FromSeconds(30),
                pollInterval ?? TimeSpan.FromMinutes(15));
        }
    }

    private async Task PollAsync()
    {
        if (this.Mode == AutomaticUpdateMode.Off || !this.scheduler.Poll())
        {
            return;
        }

        try
        {
            var availability = await this.CheckForUpdatesAsync().ConfigureAwait(false);
            if (availability.IsUpdateAvailable && this.Mode == AutomaticUpdateMode.DownloadAndInstall)
            {
                await this.DownloadInstallAndRelaunchAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A failed periodic check is surfaced on the next check; never crash the background timer.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.periodicTimer?.Dispose();
            this.periodicTimer = null;
        }
    }

    private static string NormalizeVersion(string tag)
        => SemanticVersion.TryParse(tag, out var version) ? version.ToString() : tag.TrimStart('v', 'V');
}
