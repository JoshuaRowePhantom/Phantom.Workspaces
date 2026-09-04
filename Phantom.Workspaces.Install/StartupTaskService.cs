namespace Phantom.Workspaces.Install;

/// <summary>
/// Registers/unregisters the per-user "run at logon" entry. It uses <see cref="IStartupRegistration"/>
/// (an <c>HKCU\...\Run</c> value in production) as the run-at-logon mechanism — this needs no
/// elevation, unlike a root Task Scheduler entry, which caused the "Access is denied" crash
/// (issue #1349). The entry always targets the stable <c>app\current\Phantom.Workspaces.exe</c>
/// path so it survives version changes, and enabling is idempotent. On enable/disable it also
/// best-effort removes any legacy <c>Phantom.Workspaces Startup</c> scheduled task a prior version
/// created via <c>schtasks</c>, so the app never double-launches at logon.
/// </summary>
public sealed class StartupTaskService
{
    /// <summary>The legacy per-user logon scheduled-task name (removed on enable/disable).</summary>
    public const string StartupTaskName = "Phantom.Workspaces Startup";

    /// <summary>The HKCU Run value name used for the run-at-logon entry.</summary>
    public const string StartupRunValueName = "Phantom.Workspaces";

    /// <summary>The argument the logon entry passes to start minimized to the tray.</summary>
    public const string StartupArgument = "--startup";

    private readonly IStartupRegistration startupRegistration;
    private readonly IScheduledTasks scheduledTasks;
    private readonly string currentExecutablePath;

    /// <summary>
    /// Creates the service over <paramref name="startupRegistration"/> (the run-at-logon mechanism)
    /// and <paramref name="scheduledTasks"/> (used only to clean up a legacy scheduled task),
    /// targeting <paramref name="currentExecutablePath"/> (typically
    /// <see cref="InstallLayout.CurrentExecutablePath"/>).
    /// </summary>
    public StartupTaskService(
        IStartupRegistration startupRegistration,
        IScheduledTasks scheduledTasks,
        string currentExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(startupRegistration);
        ArgumentNullException.ThrowIfNull(scheduledTasks);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentExecutablePath);
        this.startupRegistration = startupRegistration;
        this.scheduledTasks = scheduledTasks;
        this.currentExecutablePath = currentExecutablePath;
    }

    /// <summary>Whether the run-at-logon entry is currently registered.</summary>
    public bool IsEnabled() => this.startupRegistration.IsEnabled(StartupRunValueName);

    /// <summary>Registers (or re-points) the run-at-logon entry targeting <c>current</c>. Idempotent.</summary>
    public void Enable()
    {
        this.startupRegistration.Enable(StartupRunValueName, this.BuildStartupCommandLine());
        this.TryRemoveLegacyScheduledTask();
    }

    /// <summary>Removes the run-at-logon entry if present. Idempotent.</summary>
    public void Disable()
    {
        this.startupRegistration.Disable(StartupRunValueName);
        this.TryRemoveLegacyScheduledTask();
    }

    private string BuildStartupCommandLine()
        => $"\"{this.currentExecutablePath}\" {StartupArgument}";

    private void TryRemoveLegacyScheduledTask()
    {
        try
        {
            if (this.scheduledTasks.Exists(StartupTaskName))
            {
                this.scheduledTasks.Unregister(StartupTaskName);
            }
        }
        catch
        {
            // Best-effort: a prior version's schtasks-based logon task is being superseded by the
            // HKCU Run entry. If we cannot remove it (e.g. it is owned by an elevated principal),
            // never let that failure surface — the run-at-logon entry has already been written.
        }
    }
}
