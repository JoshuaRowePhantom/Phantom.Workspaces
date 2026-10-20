using System.Diagnostics;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;

namespace Phantom.Workspaces.Tools;

internal sealed class VsCodeTunnelInteractiveLoginLauncher
{
    private readonly Func<ProcessStartInfo, CancellationToken, Task<int>> runVisibleAsync;

    public VsCodeTunnelInteractiveLoginLauncher(
        Func<ProcessStartInfo, CancellationToken, Task<int>>? runVisibleAsync = null)
    {
        this.runVisibleAsync = runVisibleAsync ?? RunVisibleAsync;
    }

    internal async Task<int> LaunchAsync(
        string cliPath,
        VsCodeTunnelProvider provider,
        ProviderInteractionPermit permit,
        ProviderAcquisitionKey key,
        CancellationToken cancellationToken)
    {
        if (!permit.AuthorizesProvider(CredentialExecutorKind.CliOwned, key))
        {
            throw new InvalidOperationException("CLI interaction was not authorized.");
        }

        var providerArgument = provider switch
        {
            VsCodeTunnelProvider.GitHub => "github",
            VsCodeTunnelProvider.Microsoft => "microsoft",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        var startInfo = VsCodeCliLocator.BuildProcessStartInfo(
            cliPath,
            ["tunnel", "user", "login", "--provider", providerArgument]);
        startInfo.RedirectStandardInput = false;
        startInfo.RedirectStandardOutput = false;
        startInfo.RedirectStandardError = false;
        startInfo.CreateNoWindow = false;
        startInfo.Environment.Remove("VSCODE_CLI_ACCESS_TOKEN");
        return await this.runVisibleAsync(startInfo, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunVisibleAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }
}
