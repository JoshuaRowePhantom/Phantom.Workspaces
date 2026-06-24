namespace Phantom.Workspaces.Install;

/// <summary>Defines a per-user logon-triggered scheduled task.</summary>
public sealed record ScheduledTaskDefinition
{
    /// <summary>The task name (e.g. <c>Phantom.Workspaces Startup</c>).</summary>
    public required string TaskName { get; init; }

    /// <summary>The executable the task launches (the stable <c>current</c> path).</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>The arguments passed to the executable.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Wraps Windows Task Scheduler so <see cref="StartupTaskService"/> is unit-testable against a
/// fake. <see cref="Register"/> is idempotent: registering an existing task re-points it.
/// </summary>
public interface IScheduledTasks
{
    /// <summary>Whether a task named <paramref name="taskName"/> exists.</summary>
    bool Exists(string taskName);

    /// <summary>Creates or replaces the task described by <paramref name="definition"/>.</summary>
    void Register(ScheduledTaskDefinition definition);

    /// <summary>Removes the task named <paramref name="taskName"/> if it exists.</summary>
    void Unregister(string taskName);
}
