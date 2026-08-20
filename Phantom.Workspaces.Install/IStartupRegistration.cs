namespace Phantom.Workspaces.Install;

/// <summary>
/// Abstracts the per-user "run at logon" registration mechanism so <see cref="StartupTaskService"/>
/// is unit-testable against a fake. The production implementation
/// (<see cref="RegistryStartupRegistration"/>) writes an <c>HKCU\...\CurrentVersion\Run</c> value,
/// which requires no elevation — unlike a root Task Scheduler entry.
/// </summary>
public interface IStartupRegistration
{
    /// <summary>Whether a run-at-logon entry named <paramref name="valueName"/> exists.</summary>
    bool IsEnabled(string valueName);

    /// <summary>
    /// Creates or replaces the run-at-logon entry named <paramref name="valueName"/> so it launches
    /// <paramref name="commandLine"/> at user logon. Idempotent.
    /// </summary>
    void Enable(string valueName, string commandLine);

    /// <summary>Removes the run-at-logon entry named <paramref name="valueName"/> if present. Idempotent.</summary>
    void Disable(string valueName);
}
