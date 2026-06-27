using System.Runtime.Versioning;
using Phantom.Workspaces;

namespace Phantom.Workspaces.Install;

/// <summary>
/// The production <see cref="IScheduledTasks"/> backed by the Windows <c>schtasks</c> command. It
/// registers a per-user logon-triggered task (no elevation required).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealScheduledTasks : IScheduledTasks
{
    /// <inheritdoc />
    public bool Exists(string taskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        return RunSchtasks("/Query", "/TN", taskName).ExitCode == 0;
    }

    /// <inheritdoc />
    public void Register(ScheduledTaskDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.TaskName);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ExecutablePath);

        var commandLine = BuildTaskRunCommand(definition);
        var result = RunSchtasks(
            "/Create",
            "/F",
            "/SC",
            "ONLOGON",
            "/TN",
            definition.TaskName,
            "/TR",
            commandLine);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? string.Empty
                : $"\n{result.StandardError}";
            throw new InvalidOperationException(
                $"schtasks failed (exit {result.ExitCode}) registering '{definition.TaskName}'.{detail}");
        }
    }

    /// <inheritdoc />
    public void Unregister(string taskName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        if (!this.Exists(taskName))
        {
            return;
        }

        var result = RunSchtasks("/Delete", "/F", "/TN", taskName);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? string.Empty
                : $"\n{result.StandardError}";
            throw new InvalidOperationException(
                $"schtasks failed (exit {result.ExitCode}) deleting '{taskName}'.{detail}");
        }
    }

    internal static string BuildTaskRunCommand(ScheduledTaskDefinition definition)
    {
        var command = $"\"{definition.ExecutablePath}\"";
        if (definition.Arguments.Count > 0)
        {
            command += " " + string.Join(' ', definition.Arguments);
        }

        return command;
    }

    private static ProcessResult RunSchtasks(params string[] arguments)
    {
        return ProcessRunner.RunProcessAsync(
            new RunProcessParameters(
                Command: "schtasks.exe",
                Arguments: arguments))
            .GetAwaiter().GetResult();
    }
}
