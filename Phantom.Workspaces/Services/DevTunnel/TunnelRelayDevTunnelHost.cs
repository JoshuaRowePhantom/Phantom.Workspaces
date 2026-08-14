using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DevTunnels.Connections;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Ssh;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Concrete <see cref="IDevTunnelRelayHost"/> wrapping the Dev Tunnels SDK
/// <see cref="TunnelRelayTunnelHost"/>. Thin SDK glue: starts an in-process relay host for the ensured
/// tunnel and forwards relay connections to the local listening port. The rich SDK <see cref="Tunnel"/>
/// to host is read from the management wrapper that ensured it.
/// </summary>
internal sealed class TunnelRelayDevTunnelHost : IDevTunnelRelayHost
{
    private readonly DevTunnelManagementClientWrapper managementClientWrapper;
    private TunnelRelayTunnelHost? relayHost;

    public TunnelRelayDevTunnelHost(DevTunnelManagementClientWrapper managementClientWrapper)
    {
        this.managementClientWrapper = managementClientWrapper ?? throw new ArgumentNullException(nameof(managementClientWrapper));
    }

    public bool IsRunning => this.relayHost is not null;

    public async Task StartAsync(string tunnelId, int localPort, CancellationToken cancellationToken = default)
    {
        // Fetch a fresh, connect-ready tunnel (includes the forwarded ports the SDK relay host requires;
        // the cached tunnel's ports were cleared by the access-control update, which cannot carry ports).
        var tunnel = await this.managementClientWrapper
            .GetConnectReadyTunnelAsync(tunnelId, cancellationToken)
            .ConfigureAwait(false);

        var host = new TunnelRelayTunnelHost(
            this.managementClientWrapper.ManagementClient,
            new TraceSource(nameof(TunnelRelayDevTunnelHost)));
        await host.ConnectAsync(tunnel, new TunnelConnectionOptions { EnableRetry = true }, cancellationToken).ConfigureAwait(false);
        this.relayHost = host;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (this.relayHost is not null)
        {
            var host = this.relayHost;
            this.relayHost = null;
            await DisposeRelayHostSafelyAsync(host).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Disposes the SDK relay host and consumes the terminal exceptions that are the expected
    /// outcome of tearing down the relay host mid-flight. The SDK runs fire-and-forget background
    /// tasks (per-channel <c>TcpClient.ConnectAsync</c> loops, the SSH server-session message loop).
    /// When we dispose the host, whichever of those tasks is in flight resolves with its own
    /// terminal exception; if the SDK's DisposeAsync surfaces it, we consume it here at the site
    /// that owns the SDK object (issue #1301). Anything not in the expected shutdown-outcome set
    /// re-throws so genuine defects still surface.
    /// </summary>
    internal static async Task DisposeRelayHostSafelyAsync(IAsyncDisposable host)
    {
        ArgumentNullException.ThrowIfNull(host);
        try
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedShutdownException(exception))
        {
            // Expected terminal outcome for the SDK's fire-and-forget background work — consume.
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> iff <paramref name="exception"/> — including every inner
    /// exception of any <see cref="AggregateException"/> chain — is one of the expected relay-host
    /// shutdown outcomes: <see cref="SocketException"/> with <see cref="SocketError.OperationAborted"/>,
    /// <see cref="SshConnectionException"/> (SSH-session-disposed marker), <see cref="OperationCanceledException"/>,
    /// or <see cref="ObjectDisposedException"/>.
    /// </summary>
    internal static bool IsExpectedShutdownException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is AggregateException aggregate)
        {
            var flat = aggregate.Flatten();
            if (flat.InnerExceptions.Count == 0)
            {
                return false;
            }

            foreach (var inner in flat.InnerExceptions)
            {
                if (!IsExpectedShutdownException(inner))
                {
                    return false;
                }
            }

            return true;
        }

        return exception switch
        {
            OperationCanceledException => true,
            ObjectDisposedException => true,
            SocketException socket => socket.SocketErrorCode == SocketError.OperationAborted,
            SshConnectionException => true,
            _ => false,
        };
    }
}
