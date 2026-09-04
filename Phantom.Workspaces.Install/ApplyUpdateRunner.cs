namespace Phantom.Workspaces.Install;

/// <summary>
/// Implements the short-lived <c>--apply-update &lt;stagedVersionDir&gt; [--relaunch]</c> mode. It
/// waits for the previous instance to release its single-instance lock, atomically repoints
/// <c>current</c> at the staged version, retains the previous version for rollback (recorded with
/// the health gate), prunes other superseded versions, and optionally relaunches the now-current
/// executable. A failure before the repoint leaves <c>current</c> untouched and returns
/// <see cref="ExitCode.UpdateApplyFailure"/>.
/// </summary>
public sealed class ApplyUpdateRunner
{
    /// <summary>The default bounded wait for the previous instance to release its lock.</summary>
    public static readonly TimeSpan DefaultLockWaitTimeout = TimeSpan.FromSeconds(30);

    private readonly InstallLayout layout;
    private readonly IInstanceReleaseWaiter releaseWaiter;
    private readonly HealthGate healthGate;
    private readonly IProcessLauncher processLauncher;
    private readonly TimeSpan lockWaitTimeout;

    /// <summary>Creates the runner over its collaborators.</summary>
    public ApplyUpdateRunner(
        InstallLayout layout,
        IInstanceReleaseWaiter releaseWaiter,
        HealthGate healthGate,
        IProcessLauncher processLauncher,
        TimeSpan? lockWaitTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(releaseWaiter);
        ArgumentNullException.ThrowIfNull(healthGate);
        ArgumentNullException.ThrowIfNull(processLauncher);
        this.layout = layout;
        this.releaseWaiter = releaseWaiter;
        this.healthGate = healthGate;
        this.processLauncher = processLauncher;
        this.lockWaitTimeout = lockWaitTimeout ?? DefaultLockWaitTimeout;
    }

    /// <summary>
    /// Runs the apply flow for the staged <paramref name="stagedVersionDirectory"/>, relaunching
    /// when <paramref name="relaunch"/> is set. Returns the process exit code.
    /// </summary>
    public async Task<ExitCode> RunAsync(
        string stagedVersionDirectory,
        bool relaunch,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedVersionDirectory);

        var version = Path.GetFileName(
            stagedVersionDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(version))
        {
            return ExitCode.UpdateApplyFailure;
        }

        if (!await this.releaseWaiter.WaitForReleaseAsync(this.lockWaitTimeout, cancellationToken).ConfigureAwait(false))
        {
            // The previous instance never released the lock; do not touch current.
            return ExitCode.UpdateApplyFailure;
        }

        string? previousVersion;
        try
        {
            previousVersion = this.layout.ResolveCurrentVersion();
            this.layout.RepointCurrent(version);
        }
        catch (Exception exception) when (exception is IOException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            // Repoint failed; current is left untouched.
            return ExitCode.UpdateApplyFailure;
        }

        this.healthGate.MarkApplied(version, previousVersion);
        this.layout.PruneVersions(keepVersion: version, alsoKeepVersion: previousVersion);

        if (relaunch)
        {
            this.processLauncher.Start(new ProcessStartRequest
            {
                FileName = this.layout.CurrentExecutablePath,
                Arguments = new[] { StartupTaskService.StartupArgument },
                Detached = true,
            });
        }

        return ExitCode.Success;
    }
}
