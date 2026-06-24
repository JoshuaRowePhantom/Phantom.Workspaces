namespace Phantom.Workspaces.Install;

/// <summary>
/// Registers/unregisters the per-user "run at logon" scheduled task. The task always targets the
/// stable <c>app\current\Phantom.Workspaces.exe</c> path so it survives version changes, and
/// enabling is idempotent (re-points an existing task at <c>current</c>).
/// </summary>
public sealed class StartupTaskService
{
    /// <summary>The per-user logon task name.</summary>
    public const string StartupTaskName = "Phantom.Workspaces Startup";

    /// <summary>The argument the logon task passes to start minimized to the tray.</summary>
    public const string StartupArgument = "--startup";

    private readonly IScheduledTasks scheduledTasks;
    private readonly string currentExecutablePath;

    /// <summary>
    /// Creates the service over <paramref name="scheduledTasks"/>, targeting
    /// <paramref name="currentExecutablePath"/> (typically <see cref="InstallLayout.CurrentExecutablePath"/>).
    /// </summary>
    public StartupTaskService(IScheduledTasks scheduledTasks, string currentExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(scheduledTasks);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentExecutablePath);
        this.scheduledTasks = scheduledTasks;
        this.currentExecutablePath = currentExecutablePath;
    }

    /// <summary>Whether the logon task is currently registered.</summary>
    public bool IsEnabled() => this.scheduledTasks.Exists(StartupTaskName);

    /// <summary>Registers (or re-points) the logon task targeting <c>current</c>. Idempotent.</summary>
    public void Enable()
    {
        this.scheduledTasks.Register(new ScheduledTaskDefinition
        {
            TaskName = StartupTaskName,
            ExecutablePath = this.currentExecutablePath,
            Arguments = new[] { StartupArgument },
        });
    }

    /// <summary>Removes the logon task if present. Idempotent.</summary>
    public void Disable() => this.scheduledTasks.Unregister(StartupTaskName);
}
