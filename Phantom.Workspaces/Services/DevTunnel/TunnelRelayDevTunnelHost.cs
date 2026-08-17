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
    private CancellationTokenSource? shutdownCts;

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

        // Issue #1322: connect under a dedicated shutdown token (linked to the caller's token) so the
        // SDK's in-flight SSH session requests (SshSession.RequestAsync -> SendMessageAsync) can be
        // cancelled at teardown BEFORE the underlying SshSession is disposed. Cancelling first makes a
        // pending request complete with OperationCanceledException (an expected shutdown outcome the SDK
        // itself observes) instead of racing disposal into an unobserved ObjectDisposedException("SshSession").
        this.shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var host = new TunnelRelayTunnelHost(
            this.managementClientWrapper.ManagementClient,
            new TraceSource(nameof(TunnelRelayDevTunnelHost)));
        await host.ConnectAsync(tunnel, new TunnelConnectionOptions { EnableRetry = true }, this.shutdownCts.Token).ConfigureAwait(false);
        this.relayHost = host;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (this.relayHost is not null)
        {
            var host = this.relayHost;
            var cts = this.shutdownCts;
            this.relayHost = null;
            this.shutdownCts = null;
            await CancelAndDisposeRelayHostSafelyAsync(host, cts).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the shutdown token, then disposes the SDK relay host, then disposes the token source.
    /// Issue #1322: cancelling <paramref name="shutdownCts"/> <em>before</em> disposal is the whole
    /// point — any in-flight <c>SshSession.RequestAsync(SessionRequestMessage, cancellation)</c> the
    /// SDK is holding honours the token and completes with <see cref="OperationCanceledException"/>
    /// (an expected shutdown outcome the SDK observes) instead of racing the disposal of the underlying
    /// <c>SshSession</c> into an unobserved <see cref="ObjectDisposedException"/> that escapes #1301's
    /// guard (which only wraps the Task returned by <c>DisposeAsync</c>) and reaches
    /// <see cref="TaskScheduler.UnobservedTaskException"/>. Extracted as an internal seam so the
    /// cancel-then-dispose ordering is regressible without a live SDK relay host.
    /// </summary>
    internal static async Task CancelAndDisposeRelayHostSafelyAsync(IAsyncDisposable host, CancellationTokenSource? shutdownCts)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (shutdownCts is not null)
        {
            try
            {
                shutdownCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The token source was already disposed by a concurrent/prior teardown — nothing to cancel.
            }
        }

        try
        {
            await DisposeRelayHostSafelyAsync(host).ConfigureAwait(false);
        }
        finally
        {
            shutdownCts?.Dispose();
        }
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
