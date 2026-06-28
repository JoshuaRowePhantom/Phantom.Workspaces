using System.Diagnostics;
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

    private readonly ICurrentExecutionContextProvider currentExecutionContextProvider;
    private readonly Func<string, string, CancellationToken, Task<(string Output, int ExitCode)>>? cliRunner;
    private readonly Func<string> defaultCliPathResolver;

    public RunVsCodeTunnelTool(
        ICurrentExecutionContextProvider? currentExecutionContextProvider = null,
        Func<string, string, CancellationToken, Task<(string Output, int ExitCode)>>? cliRunner = null,
        Func<string>? defaultCliPathResolver = null)
    {
        this.currentExecutionContextProvider = currentExecutionContextProvider ?? new CurrentExecutionContextProvider();
        this.cliRunner = cliRunner;
        this.defaultCliPathResolver = defaultCliPathResolver ?? VsCodeCliLocator.ResolveDefaultCliPath;
    }

    public string ToolType => "run-vscode-tunnel";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var cliPath = this.ResolveCliPath(context.Tool.Data);
        var tunnelName = this.ResolveTunnelName(context.Tool.Data);

        var (status, statusError) = await this.GetServiceStatusAsync(cliPath, context.CancellationToken).ConfigureAwait(false);

        if (status == VsCodeTunnelServiceStatus.CliNotFound)
            return WorkspaceToolExecutionResult.Failure($"Failed to start VS Code CLI: {statusError}");

        if (status == VsCodeTunnelServiceStatus.NotInstalled)
        {
            var installExitCode = await this.InstallServiceAsync(cliPath, tunnelName, context.CancellationToken).ConfigureAwait(false);
            if (installExitCode != 0)
                return WorkspaceToolExecutionResult.Failure($"Failed to install VS Code tunnel service: exit code {installExitCode}");
        }
        else if (status == VsCodeTunnelServiceStatus.Invalid)
        {
            var uninstallExitCode = await this.UninstallServiceAsync(cliPath, context.CancellationToken).ConfigureAwait(false);
            if (uninstallExitCode != 0)
                return WorkspaceToolExecutionResult.Failure($"Failed to uninstall VS Code tunnel service: exit code {uninstallExitCode}");

            var installExitCode = await this.InstallServiceAsync(cliPath, tunnelName, context.CancellationToken).ConfigureAwait(false);
            if (installExitCode != 0)
                return WorkspaceToolExecutionResult.Failure($"Failed to install VS Code tunnel service: exit code {installExitCode}");
        }

        if (status != VsCodeTunnelServiceStatus.Running)
        {
            var (postInstallStatus, _) = await this.GetServiceStatusAsync(cliPath, context.CancellationToken).ConfigureAwait(false);
            if (postInstallStatus != VsCodeTunnelServiceStatus.Running)
                return WorkspaceToolExecutionResult.Failure("VS Code tunnel service did not start after installation");
        }

        return WorkspaceToolExecutionResult.Success();
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

    private async Task<(VsCodeTunnelServiceStatus Status, string? ErrorMessage)> GetServiceStatusAsync(
        string cliPath, CancellationToken cancellationToken)
    {
        var runner = this.cliRunner ?? DefaultRunCliAsync;
        try
        {
            var (output, exitCode) = await runner(cliPath, "tunnel service log", cancellationToken).ConfigureAwait(false);

            if (exitCode == 0 && output.Contains("running", StringComparison.OrdinalIgnoreCase))
                return (VsCodeTunnelServiceStatus.Running, null);

            if (exitCode != 0)
                return (VsCodeTunnelServiceStatus.NotInstalled, null);

            return (VsCodeTunnelServiceStatus.Invalid, null);
        }
        catch (Exception ex)
        {
            return (VsCodeTunnelServiceStatus.CliNotFound, ex.Message);
        }
    }

    private async Task<int> UninstallServiceAsync(string cliPath, CancellationToken cancellationToken)
    {
        var runner = this.cliRunner ?? DefaultRunCliAsync;
        var (_, exitCode) = await runner(cliPath, "tunnel service uninstall", cancellationToken).ConfigureAwait(false);
        return exitCode;
    }

    private async Task<int> InstallServiceAsync(string cliPath, string tunnelName, CancellationToken cancellationToken)
    {
        var runner = this.cliRunner ?? DefaultRunCliAsync;
        var (_, exitCode) = await runner(cliPath,
            $"tunnel service install --accept-server-license-terms --name {tunnelName}",
            cancellationToken).ConfigureAwait(false);
        return exitCode;
    }

    private static async Task<(string Output, int ExitCode)> DefaultRunCliAsync(
        string cliPath, string arguments, CancellationToken cancellationToken)
    {
        var psi = VsCodeCliLocator.BuildProcessStartInfo(cliPath, arguments);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {cliPath}");

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (output, process.ExitCode);
    }
}

internal enum VsCodeTunnelServiceStatus { NotInstalled, Invalid, Running, CliNotFound }
