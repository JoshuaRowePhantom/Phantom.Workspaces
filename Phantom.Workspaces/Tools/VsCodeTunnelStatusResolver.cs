using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// Default <see cref="IVsCodeTunnelStatusResolver"/>. This is the single component responsible for
/// invoking <c>code tunnel status</c> and parsing its result; both <see cref="RunVsCodeTunnelTool"/>
/// and <see cref="VsCodeTunnelDiscoveryTool"/> obtain tunnel status exclusively through it so they
/// can never disagree about liveness/health. <c>code tunnel status</c> takes no arguments (the
/// <c>--output json</c> flag is unsupported and errors with exit 2) and always prints a single-line
/// JSON object to stdout. Liveness is derived from the strongly-typed, source-generated
/// <see cref="VsCodeTunnelStatusOutput"/>: the tunnel is RUNNING iff the outer <c>tunnel</c> member
/// is non-null, and CONNECTED iff the inner <c>tunnel</c> health string equals <c>"Connected"</c>.
/// The full CLI result (exit code, stdout, stderr) is always returned via
/// <see cref="VsCodeTunnelResolution"/> so the caller can surface it to the user on failure.
/// </summary>
public sealed class VsCodeTunnelStatusResolver : IVsCodeTunnelStatusResolver
{
    private readonly VsCodeCliInvoker invoker;
    private readonly ILogger logger;

    public VsCodeTunnelStatusResolver(
        VsCodeCliInvoker? invoker = null,
        ILogger? logger = null)
    {
        this.logger = logger ?? NullLogger.Instance;
        this.invoker = invoker ?? new VsCodeCliInvoker(notificationService: null, logger: this.logger);
    }

    public async Task<VsCodeTunnelResolution> ResolveAsync(string cliPath, CancellationToken cancellationToken)
    {
        VsCodeCliResult result;
        try
        {
            result = await this.invoker.RunAsync(
                cliPath,
                "tunnel status",
                operationDescription: "vscode tunnel status",
                VsCodeCliReporting.LogOnly,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            return new VsCodeTunnelResolution(Status: null, CliResult: null, CliLaunchError: ex.Message);
        }

        var status = ParseStatus(result.StandardOut);
        return new VsCodeTunnelResolution(status, result, CliLaunchError: null);
    }

    /// <summary>
    /// Parses <c>code tunnel status</c> stdout into a <see cref="VsCodeTunnelStatus"/>. Returns
    /// <see langword="null"/> when no tunnel daemon is running (outer <c>tunnel</c> member is null)
    /// or the output cannot be parsed as the expected JSON object.
    /// </summary>
    internal static VsCodeTunnelStatus? ParseStatus(string? stdout)
    {
        if (!TryDeserialize(stdout, out var output) || output?.Tunnel is null)
        {
            return null;
        }

        var daemon = output.Tunnel;
        var name = string.IsNullOrWhiteSpace(daemon.Name) ? string.Empty : daemon.Name!;
        var url = string.IsNullOrEmpty(name)
            ? "https://vscode.dev/tunnel/"
            : $"https://vscode.dev/tunnel/{name}";
        var isConnected = string.Equals(daemon.Tunnel, "Connected", StringComparison.OrdinalIgnoreCase);

        return new VsCodeTunnelStatus(name, url, isConnected, daemon.LastFailReason);
    }

    private static bool TryDeserialize(string? stdout, out VsCodeTunnelStatusOutput? output)
    {
        output = null;
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return false;
        }

        foreach (var line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                continue;
            }

            if (TryDeserializeObject(trimmed, out output))
            {
                return true;
            }
        }

        return TryDeserializeObject(stdout.Trim(), out output);
    }

    private static bool TryDeserializeObject(string json, out VsCodeTunnelStatusOutput? output)
    {
        output = null;
        if (!json.StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            output = JsonSerializer.Deserialize(json, VsCodeTunnelStatusJsonContext.Default.VsCodeTunnelStatusOutput);
            return output is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
