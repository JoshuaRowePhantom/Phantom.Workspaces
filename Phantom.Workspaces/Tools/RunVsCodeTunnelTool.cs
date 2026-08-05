using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Services.Notifications;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// A built-in scheduled tool that keeps a VS Code dev tunnel alive on the target machine. It
/// spawns <c>code tunnel --accept-server-license-terms --name &lt;name&gt;</c> directly (never
/// the Windows-service subcommands) and does not return from <see cref="ExecuteAsync"/> until the
/// tunnel is no longer up. "Up" is a conjunction of two conditions: the spawned child process is
/// still alive AND <c>code tunnel status</c> continues to report the tunnel as running. Combined
/// with the 1-minute default schedule cadence, this reduces to: block for the entire lifetime of
/// the tunnel; the next scheduled tick simply re-spawns after either the child dies or
/// <c>code tunnel status</c> stops reporting the tunnel as running.
/// </summary>
public sealed class RunVsCodeTunnelTool : IWorkspaceTool
{
    /// <summary>Optional tool-entity property overriding the VS Code CLI executable path.</summary>
    public const string CliPathProperty = "cli-path";

    /// <summary>Optional tool-entity property overriding the tunnel name (defaults to hostname).</summary>
    public const string TunnelNameProperty = "tunnel-name";

    /// <summary>
    /// Default recurrence frequency materialised for a <c>run-vscode-tunnel</c> tool-relationship
    /// schedule participant. Because <see cref="ExecuteAsync"/> blocks for the tunnel's entire
    /// lifetime, this only governs how quickly a dead tunnel is re-spawned.
    /// </summary>
    public static TimeSpan DefaultScheduleFrequency { get; } = TimeSpan.FromMinutes(1);

    /// <summary>Timeout for the (fast) login and status sub-invocations.</summary>
    private static readonly TimeSpan CliOperationTimeout = TimeSpan.FromMinutes(5);

    private readonly ICurrentExecutionContextProvider currentExecutionContextProvider;
    private readonly ILogger<RunVsCodeTunnelTool> logger;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<(string Output, int ExitCode)>>? cliRunner;
    private readonly Func<string?> defaultCliPathResolver;
    private readonly Func<string?> tokenResolver;
    private readonly VsCodeCliInvoker cliInvoker;
    private readonly VsCodeTunnelProcessLauncher processLauncher;
    private readonly Func<CancellationToken, Task> waitBetweenPollsAsync;

    public RunVsCodeTunnelTool(
        ICurrentExecutionContextProvider? currentExecutionContextProvider = null,
        Func<string, string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<(string Output, int ExitCode)>>? cliRunner = null,
        Func<string?>? defaultCliPathResolver = null,
        Func<string?>? tokenResolver = null,
        INotificationService? notificationService = null,
        VsCodeCliInvoker? cliInvoker = null,
        VsCodeTunnelProcessLauncher? processLauncher = null,
        Func<CancellationToken, Task>? waitBetweenPollsAsync = null,
        ILogger<RunVsCodeTunnelTool>? logger = null)
    {
        this.currentExecutionContextProvider = currentExecutionContextProvider ?? new CurrentExecutionContextProvider();
        this.logger = logger ?? NullLogger<RunVsCodeTunnelTool>.Instance;
        this.cliRunner = cliRunner;
        this.defaultCliPathResolver = defaultCliPathResolver ?? (() => VsCodeCliLocator.ResolveDefaultCliPath());
        this.tokenResolver = tokenResolver ?? (() => Phantom.Workspaces.Llm.GitHubAuthTokenResolver.Resolve(this.logger));
        this.cliInvoker = cliInvoker
            ?? new VsCodeCliInvoker(notificationService: notificationService, logger: this.logger);
        this.processLauncher = processLauncher ?? DefaultProcessLauncher;
        this.waitBetweenPollsAsync = waitBetweenPollsAsync
            ?? (ct => Task.Delay(TimeSpan.FromSeconds(15), ct));
    }

    public string ToolType => "run-vscode-tunnel";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var cliPath = this.ResolveCliPath(context.Tool.Data);
        if (string.IsNullOrWhiteSpace(cliPath))
        {
            return WorkspaceToolExecutionResult.Failure("VS Code `code` CLI not found.");
        }

        var tunnelName = this.ResolveTunnelName(context.Tool.Data);

        // Best-effort GitHub pre-login. Never fatal.
        await this.TryLoginAsync(cliPath, context.CancellationToken).ConfigureAwait(false);

        // Spawn the long-lived child process directly. From this point on we own it for the
        // entire lifetime of ExecuteAsync — there is no cross-tick registry.
        var arguments = $"tunnel --accept-server-license-terms --name {tunnelName}";
        IVsCodeTunnelChildProcess child;
        try
        {
            child = this.processLauncher(cliPath, arguments);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to spawn `code tunnel`: {Message}", ex.Message);
            return WorkspaceToolExecutionResult.Failure(
                $"Failed to spawn `code tunnel`: {ex.Message}");
        }

        try
        {
            while (!context.CancellationToken.IsCancellationRequested)
            {
                if (child.HasExited)
                {
                    var exitCode = SafeExitCode(child);
                    var stderr = child.CapturedStandardError;
                    return WorkspaceToolExecutionResult.Failure(
                        $"`code tunnel` exited with code {exitCode}.\nStderr:\n{stderr}");
                }

                var statusResult = await this.RunCliAsync(
                    cliPath,
                    "tunnel status",
                    environmentVariables: null,
                    VsCodeCliReporting.LogOnly,
                    context.CancellationToken).ConfigureAwait(false);

                var reportsRunning =
                    statusResult.ExitCode == 0
                    && statusResult.StandardOut.Contains("running", StringComparison.OrdinalIgnoreCase);

                if (!reportsRunning)
                {
                    child.Kill();
                    return new WorkspaceToolExecutionResult
                    {
                        ResultContent =
                            $"`code tunnel status` no longer reports the tunnel as running "
                            + $"(exit {statusResult.ExitCode}).\n{statusResult.StandardOut}",
                    };
                }

                await this.waitBetweenPollsAsync(context.CancellationToken).ConfigureAwait(false);
            }

            child.Kill();
            context.CancellationToken.ThrowIfCancellationRequested();
            return new WorkspaceToolExecutionResult { ResultContent = "cancelled" };
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            child.Kill();
            throw;
        }
        catch (Exception)
        {
            child.Kill();
            throw;
        }
        finally
        {
            child.Dispose();
        }
    }

    private static int SafeExitCode(IVsCodeTunnelChildProcess child)
    {
        try { return child.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    private async Task TryLoginAsync(string cliPath, CancellationToken cancellationToken)
    {
        var token = this.tokenResolver();
        if (string.IsNullOrWhiteSpace(token))
        {
            this.logger.LogInformation(
                "No GitHub token available; skipping `code tunnel user login --provider github` "
                + "and relying on cached CLI credentials.");
            return;
        }

        var env = new Dictionary<string, string> { ["VSCODE_CLI_ACCESS_TOKEN"] = token };
        var loginResult = await this.RunCliAsync(
            cliPath,
            "tunnel user login --provider github",
            env,
            VsCodeCliReporting.LogAndReportOnFailure,
            cancellationToken).ConfigureAwait(false);

        if (loginResult.ExitCode != 0)
        {
            this.logger.LogWarning(
                "`code tunnel user login --provider github` exited {ExitCode}: {Output}",
                loginResult.ExitCode,
                loginResult.StandardOut);
        }
    }

    private string ResolveTunnelName(JsonElement? toolData)
    {
        if (toolData is JsonElement toolDataValue
            && toolDataValue.ValueKind == JsonValueKind.Object
            && toolDataValue.TryGetProperty(TunnelNameProperty, out var nameElement)
            && nameElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            return nameElement.GetString()!;
        }

        return this.currentExecutionContextProvider.ComputerName;
    }

    private string? ResolveCliPath(JsonElement? toolData)
    {
        if (toolData is JsonElement toolDataValue
            && toolDataValue.ValueKind == JsonValueKind.Object
            && toolDataValue.TryGetProperty(CliPathProperty, out var pathElement)
            && pathElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(pathElement.GetString()))
        {
            return pathElement.GetString();
        }

        return this.defaultCliPathResolver();
    }

    private async Task<VsCodeCliResult> RunCliAsync(
        string cliPath,
        string arguments,
        IReadOnlyDictionary<string, string>? environmentVariables,
        VsCodeCliReporting reporting,
        CancellationToken cancellationToken)
    {
        if (this.cliRunner is not null)
        {
            var (output, exitCode) = await this.cliRunner(cliPath, arguments, environmentVariables, cancellationToken).ConfigureAwait(false);
            return new VsCodeCliResult(exitCode, output, string.Empty);
        }

        return await this.cliInvoker.RunAsync(
            cliPath,
            arguments,
            operationDescription: $"vscode {arguments}",
            reporting,
            environmentVariables,
            CliOperationTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static IVsCodeTunnelChildProcess DefaultProcessLauncher(string cliPath, string arguments)
    {
        var parameters = VsCodeCliLocator.BuildRunProcessParameters(
            cliPath, arguments, timeout: null, environmentVariables: null);

        var psi = new ProcessStartInfo(parameters.Command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in parameters.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        return new ProcessBackedVsCodeTunnelChildProcess(process);
    }
}
