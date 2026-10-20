using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Tools;

public sealed class VsCodeTunnelStatusResolver : IVsCodeTunnelStatusResolver
{
    private readonly VsCodeCliInvoker invoker;

    public VsCodeTunnelStatusResolver(
        VsCodeCliInvoker? invoker = null,
        ILogger? logger = null)
    {
        this.invoker = invoker ?? new VsCodeCliInvoker();
    }

    public async Task<VsCodeTunnelResolution> ResolveAsync(
        string cliPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await this.invoker.ProbeAsync(
                cliPath,
                ["tunnel", "status"],
                static (exitCode, standardOutput, _) => exitCode == 0
                    ? ParseStatus(standardOutput)
                    : new VsCodeTunnelResolution { Outcome = VsCodeTunnelOutcome.CommandFailed },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new VsCodeTunnelResolution { Outcome = VsCodeTunnelOutcome.Cancelled };
        }
        catch (Exception exception) when (exception is Win32Exception or TimeoutException or InvalidOperationException)
        {
            return new VsCodeTunnelResolution { Outcome = VsCodeTunnelOutcome.CommandFailed };
        }
    }

    internal static VsCodeTunnelResolution ParseStatus(string? standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput))
        {
            return new VsCodeTunnelResolution { Outcome = VsCodeTunnelOutcome.InvalidStatus };
        }

        try
        {
            using var document = JsonDocument.Parse(standardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tunnel", out var tunnel))
            {
                return new VsCodeTunnelResolution { Outcome = VsCodeTunnelOutcome.InvalidStatus };
            }

            if (tunnel.ValueKind == JsonValueKind.Null)
            {
                return new VsCodeTunnelResolution { Outcome = VsCodeTunnelOutcome.NotRunning };
            }

            if (tunnel.ValueKind != JsonValueKind.Object
                || !tunnel.TryGetProperty("tunnel", out var health)
                || health.ValueKind != JsonValueKind.String)
            {
                return new VsCodeTunnelResolution { Outcome = VsCodeTunnelOutcome.InvalidStatus };
            }

            var connected = string.Equals(health.GetString(), "Connected", StringComparison.OrdinalIgnoreCase);
            var name = tunnel.TryGetProperty("name", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            if (name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            {
                return new VsCodeTunnelResolution { Outcome = VsCodeTunnelOutcome.InvalidStatus };
            }

            return new VsCodeTunnelResolution
            {
                Outcome = connected ? VsCodeTunnelOutcome.Ready : VsCodeTunnelOutcome.RunningDisconnected,
                Status = new VsCodeTunnelStatus(name, $"https://vscode.dev/tunnel/{name}", connected),
            };
        }
        catch (JsonException)
        {
            return new VsCodeTunnelResolution { Outcome = VsCodeTunnelOutcome.InvalidStatus };
        }
    }
}
