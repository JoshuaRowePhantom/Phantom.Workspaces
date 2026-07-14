using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// A built-in scheduled tool that ensures the VS Code dev tunnel service is installed and running
/// on the target machine.
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

    public RunVsCodeTunnelTool(
        ICurrentExecutionContextProvider? currentExecutionContextProvider = null,
        Func<string, string, IReadOnlyDictionary<string, string>?, CancellationToken, Task<(string Output, int ExitCode)>>? cliRunner = null,
        Func<string>? defaultCliPathResolver = null,
        Func<string?>? tokenResolver = null,
        ILogger<RunVsCodeTunnelTool>? logger = null)
    {
        this.currentExecutionContextProvider = currentExecutionContextProvider ?? new CurrentExecutionContextProvider();
        this.logger = logger ?? NullLogger<RunVsCodeTunnelTool>.Instance;
        this.cliRunner = cliRunner;
        this.defaultCliPathResolver = defaultCliPathResolver ?? VsCodeCliLocator.ResolveDefaultCliPath;
        this.tokenResolver = tokenResolver ?? (() => Phantom.Workspaces.Llm.GitHubAuthTokenResolver.Resolve(this.logger));
    }

    public string ToolType => "run-vscode-tunnel";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var cliPath = this.ResolveCliPath(context.Tool.Data);
            var tunnelName = this.ResolveTunnelName(context.Tool.Data);

            var (status, statusError) = await this.GetServiceStatusAsync(cliPath, context.CancellationToken).ConfigureAwait(false);

            if (status == VsCodeTunnelServiceStatus.CliNotFound)
                return WorkspaceToolExecutionResult.Failure($"Failed to start VS Code CLI: {statusError}");

            if (status == VsCodeTunnelServiceStatus.NotInstalled)
            {
                var installExitCode = await this.InstallServiceAsync(cliPath, tunnelName, context.CancellationToken).ConfigureAwait(false);
                if (installExitCode == -1)
                    return WorkspaceToolExecutionResult.Failure("No GitHub authentication token available. Please sign in to GitHub via the app or set the GITHUB_TOKEN environment variable.");
                if (installExitCode != 0)
                    return WorkspaceToolExecutionResult.Failure($"Failed to install VS Code tunnel service: exit code {installExitCode}");
            }
            else if (status == VsCodeTunnelServiceStatus.Stopped)
            {
                var uninstallExitCode = await this.UninstallServiceAsync(cliPath, context.CancellationToken).ConfigureAwait(false);
                if (uninstallExitCode != 0)
                    return WorkspaceToolExecutionResult.Failure($"Failed to uninstall VS Code tunnel service: exit code {uninstallExitCode}");

                var installExitCode = await this.InstallServiceAsync(cliPath, tunnelName, context.CancellationToken).ConfigureAwait(false);
                if (installExitCode == -1)
                    return WorkspaceToolExecutionResult.Failure("No GitHub authentication token available. Please sign in to GitHub via the app or set the GITHUB_TOKEN environment variable.");
                if (installExitCode != 0)
                    return WorkspaceToolExecutionResult.Failure($"Failed to install VS Code tunnel service: exit code {installExitCode}");
            }

            if (status != VsCodeTunnelServiceStatus.Running)
            {
                var (postInstallStatus, _) = await this.GetServiceStatusAsync(cliPath, context.CancellationToken).ConfigureAwait(false);
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
        var runner = this.cliRunner ?? DefaultRunCliAsync;
        try
        {
            var (output, exitCode) = await runner(cliPath, "tunnel status", null, cancellationToken).ConfigureAwait(false);
            return exitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<(VsCodeTunnelServiceStatus Status, string? ErrorMessage)> GetServiceStatusAsync(
        string cliPath, CancellationToken cancellationToken)
    {
        var runner = this.cliRunner ?? DefaultRunCliAsync;
        try
        {
            var (output, exitCode) = await runner(cliPath, "tunnel service status", null, cancellationToken).ConfigureAwait(false);

            if (exitCode == 0 && output.Contains("running", StringComparison.OrdinalIgnoreCase))
                return (VsCodeTunnelServiceStatus.Running, null);

            if (exitCode != 0)
                return (VsCodeTunnelServiceStatus.NotInstalled, null);

            return (VsCodeTunnelServiceStatus.Stopped, null);
        }
        catch (Exception ex)
        {
            return (VsCodeTunnelServiceStatus.CliNotFound, ex.Message);
        }
    }

    private async Task<int> UninstallServiceAsync(string cliPath, CancellationToken cancellationToken)
    {
        var runner = this.cliRunner ?? DefaultRunCliAsync;
        var (_, exitCode) = await runner(cliPath, "tunnel service uninstall", null, cancellationToken).ConfigureAwait(false);
        return exitCode;
    }

    private async Task<int> InstallServiceAsync(string cliPath, string tunnelName, CancellationToken cancellationToken)
    {
        var runner = this.cliRunner ?? DefaultRunCliAsync;

        // Pre-login: reuse the app's already-resolved GitHub token so the CLI does not
        // fall back to an interactive device-code flow that the GUI cannot complete.
        var token = this.tokenResolver();
        if (string.IsNullOrWhiteSpace(token))
        {
            // Fail fast with a specific error surfaced by ExecuteAsync
            return -1;
        }

        // Pass the token via VSCODE_CLI_ACCESS_TOKEN on the child process env,
        // NOT on the command line.
        var env = new Dictionary<string, string> { ["VSCODE_CLI_ACCESS_TOKEN"] = token };
        var (_, loginExitCode) = await runner(
            cliPath,
            "tunnel user login --provider github",
            env,
            cancellationToken).ConfigureAwait(false);

        if (loginExitCode != 0)
        {
            // Likely: token lacks required scopes. Return the exit code so ExecuteAsync
            // can surface a user-facing error message.
            return loginExitCode;
        }

        var (_, exitCode) = await runner(cliPath,
            $"tunnel service install --accept-server-license-terms --name {tunnelName}",
            null,
            cancellationToken).ConfigureAwait(false);
        return exitCode;
    }

    private async Task<(string Output, int ExitCode)> DefaultRunCliAsync(
        string cliPath, string arguments, IReadOnlyDictionary<string, string>? environmentVariables, CancellationToken cancellationToken)
    {
        var parameters = VsCodeCliLocator.BuildRunProcessParameters(
            cliPath,
            arguments,
            TunnelOperationTimeout,
            environmentVariables);
        var result = await ProcessRunner.RunAndLogAsync(
            parameters,
            this.logger,
            operationDescription: "vscode tunnel service status",
            cancellationToken).ConfigureAwait(false);
        return (result.StandardOut, result.ExitCode);
    }
}

internal enum VsCodeTunnelServiceStatus { NotInstalled, Stopped, Running, CliNotFound }
