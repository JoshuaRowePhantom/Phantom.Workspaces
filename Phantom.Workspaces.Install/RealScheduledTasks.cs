using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces;

namespace Phantom.Workspaces.Install;

/// <summary>
/// The production <see cref="IScheduledTasks"/> backed by the Windows <c>schtasks</c> command. It
/// registers a per-user logon-triggered task (no elevation required).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealScheduledTasks : IScheduledTasks
{
    private readonly ILogger<RealScheduledTasks> _logger;

    public RealScheduledTasks(ILogger<RealScheduledTasks> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

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
            throw new InvalidOperationException(
                $"schtasks failed (exit {result.ExitCode}) registering '{definition.TaskName}'.");
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
            throw new InvalidOperationException(
                $"schtasks failed (exit {result.ExitCode}) deleting '{taskName}'.");
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

    private ProcessResult RunSchtasks(params string[] arguments)
    {
        return ProcessRunner.RunAndLogAsync(
            new RunProcessParameters(
                Command: "schtasks.exe",
                Arguments: arguments),
            _logger,
            operationDescription: "schtasks")
            .GetAwaiter().GetResult();
    }
}
