using System.Diagnostics;
using System.Runtime.Versioning;

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
        return RunSchtasks("/Query", "/TN", taskName) == 0;
    }

    /// <inheritdoc />
    public void Register(ScheduledTaskDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.TaskName);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.ExecutablePath);

        var commandLine = BuildTaskRunCommand(definition);
        var exitCode = RunSchtasks(
            "/Create",
            "/F",
            "/SC",
            "ONLOGON",
            "/TN",
            definition.TaskName,
            "/TR",
            commandLine);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"schtasks failed (exit {exitCode}) registering '{definition.TaskName}'.");
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

        var exitCode = RunSchtasks("/Delete", "/F", "/TN", taskName);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"schtasks failed (exit {exitCode}) deleting '{taskName}'.");
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

    private static int RunSchtasks(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("schtasks.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start schtasks.exe.");
        process.WaitForExit();
        return process.ExitCode;
    }
}
