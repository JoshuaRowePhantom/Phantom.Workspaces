using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Services.Notifications;

namespace Phantom.Workspaces.Tools;

/// <summary>Controls whether a <see cref="VsCodeCliInvoker"/> call raises user notifications.</summary>
public enum VsCodeCliReporting
{
    /// <summary>Log stdout/stderr/exit code but do not raise any user-facing notification.</summary>
    LogOnly,

    /// <summary>Log everything; raise a user-facing notification only when the CLI failed.</summary>
    LogAndReportOnFailure,

    /// <summary>Log everything; raise a user-facing notification on success and failure.</summary>
    LogAndReportAlways,
}

/// <summary>Result of a <see cref="VsCodeCliInvoker.RunAsync"/> call.</summary>
public sealed record VsCodeCliResult(int ExitCode, string StandardOut, string StandardError);

/// <summary>
/// Shared helper for invoking the VS Code <c>code</c> CLI. Every invocation is routed through
/// <see cref="ProcessRunner.RunAndLogAsync"/> so that stdout, stderr, and exit code are always
/// logged, and non-zero exits (or launch failures) are surfaced to the user via
/// <see cref="INotificationService"/>.
/// </summary>
public sealed class VsCodeCliInvoker
{
    private const int NotificationOutputCap = 4096;

    private readonly INotificationService? notificationService;
    private readonly ILogger logger;
    private readonly Func<RunProcessParameters, CancellationToken, Task<ProcessResult>> processRunner;

    public VsCodeCliInvoker(
        INotificationService? notificationService = null,
        ILogger? logger = null,
        Func<RunProcessParameters, CancellationToken, Task<ProcessResult>>? processRunner = null)
    {
        this.notificationService = notificationService;
        this.logger = logger ?? NullLogger.Instance;
        this.processRunner = processRunner ?? ((p, ct) => ProcessRunner.RunProcessAsync(p, ct));
    }

    public async Task<VsCodeCliResult> RunAsync(
        string cliPath,
        string arguments,
        string operationDescription,
        VsCodeCliReporting reporting,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var parameters = VsCodeCliLocator.BuildRunProcessParameters(
            cliPath, arguments, timeout, environmentVariables);

        this.logger.LogDebug(
            "Invoking VS Code CLI '{Command}' {Arguments} ({Operation})",
            parameters.Command,
            string.Join(' ', parameters.Arguments),
            operationDescription);

        ProcessResult processResult;
        try
        {
            processResult = await this.processRunner(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            this.logger.LogError(
                ex,
                "VS Code CLI '{Operation}' timed out after {Timeout}: {Message}",
                operationDescription,
                parameters.Timeout,
                ex.Message);
            this.TryNotify(
                operationDescription,
                heading: $"VS Code CLI timed out: {operationDescription}",
                description: $"'code {arguments}' timed out after {parameters.Timeout}.\n{ex.Message}",
                interesting: true,
                reporting,
                reportOnSuccess: false);
            throw;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            this.logger.LogError(
                ex,
                "VS Code CLI '{Operation}' could not be launched: {Message}",
                operationDescription,
                ex.Message);
            this.TryNotify(
                operationDescription,
                heading: "VS Code CLI not found",
                description: $"Could not launch 'code {arguments}': {ex.Message}\nEnsure the VS Code 'code' CLI is installed and on your PATH.",
                interesting: true,
                reporting,
                reportOnSuccess: false);
            throw;
        }

        this.LogOutcome(operationDescription, parameters.Command, arguments, processResult);
        this.ReportOutcome(operationDescription, arguments, processResult, reporting);

        return new VsCodeCliResult(processResult.ExitCode, processResult.StandardOut, processResult.StandardError);
    }

    private void LogOutcome(string operationDescription, string command, string arguments, ProcessResult result)
    {
        if (result.ExitCode != 0)
        {
            this.logger.LogWarning(
                "VS Code CLI '{Operation}' ('{Command}' {Arguments}) exited with code {ExitCode}.\nStdout:\n{Stdout}\nStderr:\n{Stderr}",
                operationDescription,
                command,
                arguments,
                result.ExitCode,
                result.StandardOut,
                result.StandardError);
        }
        else
        {
            this.logger.LogDebug(
                "VS Code CLI '{Operation}' ('{Command}' {Arguments}) exited with code {ExitCode}.\nStdout:\n{Stdout}\nStderr:\n{Stderr}",
                operationDescription,
                command,
                arguments,
                result.ExitCode,
                result.StandardOut,
                result.StandardError);
        }
    }

    private void ReportOutcome(string operationDescription, string arguments, ProcessResult result, VsCodeCliReporting reporting)
    {
        var isFailure = result.ExitCode != 0;
        var reportOnSuccess = reporting == VsCodeCliReporting.LogAndReportAlways;
        var reportOnFailure = reporting != VsCodeCliReporting.LogOnly;
        if (!isFailure && !reportOnSuccess)
        {
            return;
        }

        if (isFailure && !reportOnFailure)
        {
            return;
        }

        var heading = isFailure
            ? $"VS Code CLI failed: {operationDescription} (exit {result.ExitCode})"
            : $"VS Code CLI: {operationDescription}";
        var description = BuildDescription(arguments, result);
        this.TryNotify(operationDescription, heading, description, isFailure, reporting, reportOnSuccess);
    }

    private void TryNotify(
        string operationDescription,
        string heading,
        string description,
        bool interesting,
        VsCodeCliReporting reporting,
        bool reportOnSuccess)
    {
        if (this.notificationService is null || reporting == VsCodeCliReporting.LogOnly)
        {
            return;
        }

        this.notificationService.Notify(new Notification(
            new TabDescriptor
            {
                TabId = $"vscode-cli:{operationDescription}",
                TabTitle = "VS Code",
            },
            heading,
            description,
            DateTime.UtcNow,
            RunningState.Idle,
            interesting ? NotificationState.Interesting : NotificationState.NotInteresting));
    }

    private static string BuildDescription(string arguments, ProcessResult result)
    {
        var stdout = Truncate(result.StandardOut);
        var stderr = Truncate(result.StandardError);
        return $"code {arguments}\nExit: {result.ExitCode}\nStdout:\n{stdout}\nStderr:\n{stderr}";
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length <= NotificationOutputCap)
        {
            return value;
        }

        return value[..NotificationOutputCap] + "\n…(truncated)";
    }
}
