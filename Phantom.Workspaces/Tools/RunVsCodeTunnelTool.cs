using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Services.Notifications;
using System.Text.Json;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// A built-in scheduled tool that ensures the VS Code dev tunnel service is installed and running
/// on the target machine. All CLI invocations are routed through the shared
/// <see cref="VsCodeCliInvoker"/> so stdout, stderr, and exit codes are logged and surfaced to
/// the user via <see cref="INotificationService"/> on failure.
/// </summary>
public sealed class RunVsCodeTunnelTool : IWorkspaceTool
{
    /// <summary>Optional tool-entity property overriding the VS Code CLI executable path.</summary>
    public const string CliPathProperty = "cli-path";

    /// <summary>Optional tool-entity property overriding the tunnel name (defaults to hostname).</summary>
    public const string TunnelNameProperty = "tunnel-name";

    /// <summary>Timeout for install/uninstall/status operations.</summary>
    private static readonly TimeSpan TunnelOperationTimeout = TimeSpan.FromMinutes(5);

    private readonly ICurrentExecutionContextProvider currentExecutionContextProvider;
    private readonly ILogger<RunVsCodeTunnelTool> logger;
    private readonly Func<string, string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<(string Output, int ExitCode)>>? cliRunner;
    private readonly Func<string> defaultCliPathResolver;
    private readonly Func<string?> tokenResolver;
    private readonly VsCodeCliInvoker cliInvoker;

    public RunVsCodeTunnelTool(
        ICurrentExecutionContextProvider? currentExecutionContextProvider = null,
        Func<string, string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<(string Output, int ExitCode)>>? cliRunner = null,
        Func<string>? defaultCliPathResolver = null,
        Func<string?>? tokenResolver = null,
        INotificationService? notificationService = null,
        VsCodeCliInvoker? cliInvoker = null,
        ILogger<RunVsCodeTunnelTool>? logger = null)
    {
        this.currentExecutionContextProvider = currentExecutionContextProvider ?? new CurrentExecutionContextProvider();
        this.logger = logger ?? NullLogger<RunVsCodeTunnelTool>.Instance;
        this.cliRunner = cliRunner;
        this.defaultCliPathResolver = defaultCliPathResolver ?? VsCodeCliLocator.ResolveDefaultCliPath;
        this.tokenResolver = tokenResolver ?? (() => Phantom.Workspaces.Llm.GitHubAuthTokenResolver.Resolve(this.logger));
        this.cliInvoker = cliInvoker
            ?? new VsCodeCliInvoker(notificationService: notificationService, logger: this.logger);
    }

    public string ToolType => "run-vscode-tunnel";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var cliPath = this.ResolveCliPath(context.Tool.Data);
            var tunnelName = this.ResolveTunnelName(context.Tool.Data);

            var (status, statusOutput, statusError) = await this.GetServiceStatusAsync(cliPath, context.CancellationToken).ConfigureAwait(false);

            if (status == VsCodeTunnelServiceStatus.CliNotFound)
                return WorkspaceToolExecutionResult.Failure($"Failed to start VS Code CLI: {statusError}");

            if (status == VsCodeTunnelServiceStatus.NotInstalled)
            {
                var installResult = await this.InstallServiceAsync(cliPath, tunnelName, context.CancellationToken).ConfigureAwait(false);
                if (installResult.ExitCode == -1)
                    return WorkspaceToolExecutionResult.Failure("No GitHub authentication token available. Please sign in to GitHub via the app or set the GITHUB_TOKEN environment variable.");
                if (installResult.ExitCode != 0)
                    return WorkspaceToolExecutionResult.Failure(
                        $"Failed to install VS Code tunnel service: exit code {installResult.ExitCode}\nStdout:\n{installResult.StandardOut}\nStderr:\n{installResult.StandardError}");
            }
            else if (status == VsCodeTunnelServiceStatus.Stopped)
            {
                var uninstallResult = await this.UninstallServiceAsync(cliPath, context.CancellationToken).ConfigureAwait(false);
                if (uninstallResult.ExitCode != 0)
                    return WorkspaceToolExecutionResult.Failure(
                        $"Failed to uninstall VS Code tunnel service: exit code {uninstallResult.ExitCode}\nStdout:\n{uninstallResult.StandardOut}\nStderr:\n{uninstallResult.StandardError}");

                var installResult = await this.InstallServiceAsync(cliPath, tunnelName, context.CancellationToken).ConfigureAwait(false);
                if (installResult.ExitCode == -1)
                    return WorkspaceToolExecutionResult.Failure("No GitHub authentication token available. Please sign in to GitHub via the app or set the GITHUB_TOKEN environment variable.");
                if (installResult.ExitCode != 0)
                    return WorkspaceToolExecutionResult.Failure(
                        $"Failed to install VS Code tunnel service: exit code {installResult.ExitCode}\nStdout:\n{installResult.StandardOut}\nStderr:\n{installResult.StandardError}");
            }

            if (status != VsCodeTunnelServiceStatus.Running)
            {
                var (postInstallStatus, _, _) = await this.GetServiceStatusAsync(cliPath, context.CancellationToken).ConfigureAwait(false);
                if (postInstallStatus != VsCodeTunnelServiceStatus.Running)
                    return WorkspaceToolExecutionResult.Failure("VS Code tunnel service did not start after installation");
            }

            var tunnelStatusOutput = await this.GetTunnelStatusOutputAsync(cliPath, context.CancellationToken).ConfigureAwait(false);
            var resultContent = !string.IsNullOrWhiteSpace(tunnelStatusOutput)
                ? tunnelStatusOutput
                : "VS Code tunnel service is running.";

            return new WorkspaceToolExecutionResult { ResultContent = resultContent };
        }
        catch (TimeoutException ex)
        {
            return WorkspaceToolExecutionResult.Failure($"VS Code tunnel operation timed out: {ex.Message}");
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

    private string ResolveCliPath(JsonElement? toolData)
    {
        if (toolData is JsonElement toolDataValue
            && toolDataValue.ValueKind == JsonValueKind.Object
            && toolDataValue.TryGetProperty(CliPathProperty, out var pathElement)
            && pathElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(pathElement.GetString()))
        {
            return pathElement.GetString()!;
        }

        return this.defaultCliPathResolver();
    }

    private async Task<string?> GetTunnelStatusOutputAsync(string cliPath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await this.RunCliAsync(cliPath, "tunnel status", environmentVariables: null, VsCodeCliReporting.LogOnly, cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOut) ? result.StandardOut : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<(VsCodeTunnelServiceStatus Status, string Output, string? ErrorMessage)> GetServiceStatusAsync(
        string cliPath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await this.RunCliAsync(cliPath, "tunnel service status", environmentVariables: null, VsCodeCliReporting.LogOnly, cancellationToken).ConfigureAwait(false);

            if (result.ExitCode == 0 && result.StandardOut.Contains("running", StringComparison.OrdinalIgnoreCase))
                return (VsCodeTunnelServiceStatus.Running, result.StandardOut, null);

            if (result.ExitCode != 0)
                return (VsCodeTunnelServiceStatus.NotInstalled, result.StandardOut, null);

            return (VsCodeTunnelServiceStatus.Stopped, result.StandardOut, null);
        }
        catch (Exception ex)
        {
            return (VsCodeTunnelServiceStatus.CliNotFound, string.Empty, ex.Message);
        }
    }

    private async Task<VsCodeCliResult> UninstallServiceAsync(string cliPath, CancellationToken cancellationToken)
    {
        return await this.RunCliAsync(
            cliPath,
            "tunnel service uninstall",
            environmentVariables: null,
            VsCodeCliReporting.LogAndReportOnFailure,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<VsCodeCliResult> InstallServiceAsync(string cliPath, string tunnelName, CancellationToken cancellationToken)
    {
        // Pre-login: reuse the app's already-resolved GitHub token so the CLI does not
        // fall back to an interactive device-code flow that the GUI cannot complete.
        var token = this.tokenResolver();
        if (string.IsNullOrWhiteSpace(token))
        {
            // Fail fast with a specific error surfaced by ExecuteAsync
            return new VsCodeCliResult(-1, string.Empty, string.Empty);
        }

        // Pass the token via VSCODE_CLI_ACCESS_TOKEN on the child process env,
        // NOT on the command line.
        var env = new Dictionary<string, string> { ["VSCODE_CLI_ACCESS_TOKEN"] = token };
        var loginResult = await this.RunCliAsync(
            cliPath,
            "tunnel user login --provider github",
            env,
            VsCodeCliReporting.LogAndReportOnFailure,
            cancellationToken).ConfigureAwait(false);

        if (loginResult.ExitCode != 0)
        {
            // Likely: token lacks required scopes. Return the exit code so ExecuteAsync
            // can surface a user-facing error message.
            return loginResult;
        }

        return await this.RunCliAsync(
            cliPath,
            $"tunnel service install --accept-server-license-terms --name {tunnelName}",
            environmentVariables: null,
            VsCodeCliReporting.LogAndReportOnFailure,
            cancellationToken).ConfigureAwait(false);
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
            TunnelOperationTimeout,
            cancellationToken).ConfigureAwait(false);
    }
}

internal enum VsCodeTunnelServiceStatus { NotInstalled, Stopped, Running, CliNotFound }
