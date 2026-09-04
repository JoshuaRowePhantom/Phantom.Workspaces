using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DevTunnels.Connections;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Tcp.Events;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Production <see cref="IRelayHostSession"/> adapter wrapping a single SDK
/// <see cref="TunnelRelayTunnelHost"/> connection. Owns:
/// <list type="bullet">
/// <item>the #1322 shutdown-token ordering (cancel in-flight SSH requests before disposal);</item>
/// <item>the #1350 <c>ForwardedPortConnecting</c> handling (reject/observe local connects at teardown);</item>
/// <item>the #1375 <c>ConnectionStatusChanged</c> disconnect detection — when the SDK transitions to the
/// terminal <see cref="ConnectionStatus.Disconnected"/> for a reason other than our own teardown, it
/// raises <see cref="Disconnected"/> so the wrapper can reflect the real state and drive a reconnect.</item>
/// </list>
/// </summary>
internal sealed class SdkRelayHostSession : IRelayHostSession
{
    private readonly TunnelRelayTunnelHost host;
    private readonly CancellationTokenSource shutdownCts;
    private volatile bool shuttingDown;
    private int disconnectRaised;

    public SdkRelayHostSession(TunnelRelayTunnelHost host, CancellationToken cancellationToken)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));

        // Issue #1322: connect under a dedicated shutdown token (linked to the caller's token) so the
        // SDK's in-flight SSH session requests can be cancelled at teardown BEFORE the underlying
        // SshSession is disposed, turning a racing ObjectDisposedException into an observed cancellation.
        this.shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.host.ForwardedPortConnecting += this.OnForwardedPortConnecting;
        this.host.ConnectionStatusChanged += this.OnConnectionStatusChanged;
    }

    /// <inheritdoc />
    public event EventHandler<RelayHostDisconnectInfo>? Disconnected;

    /// <summary>Connects the SDK relay host for <paramref name="tunnel"/> under the shutdown token.</summary>
    public async Task ConnectAsync(Tunnel tunnel, CancellationToken cancellationToken)
    {
        // EnableRetry retries transient failures within a single connect attempt. Established-session
        // drops the SDK does not auto-reconnect (non-ConnectionLost reasons) are handled by the wrapper's
        // reconnect monitor, driven by the Disconnected signal below (issue #1375).
        await this.host
            .ConnectAsync(tunnel, new TunnelConnectionOptions { EnableRetry = true }, this.shutdownCts.Token)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        // Reject any further forwarded-port connections and suppress the disconnect signal before we
        // start cancelling/disposing, so no SDK work races the teardown (issues #1350, #1375).
        this.shuttingDown = true;
        this.host.ForwardedPortConnecting -= this.OnForwardedPortConnecting;
        this.host.ConnectionStatusChanged -= this.OnConnectionStatusChanged;
        await TunnelRelayDevTunnelHost.CancelAndDisposeRelayHostSafelyAsync(this.host, this.shutdownCts).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles the SDK's <c>ConnectionStatusChanged</c> event (issue #1375). When the SDK reaches the
    /// terminal <see cref="ConnectionStatus.Disconnected"/> state and we are not tearing down, raises
    /// <see cref="Disconnected"/> exactly once, mapping <see cref="SshDisconnectReason.TooManyConnections"/>
    /// to a non-retryable signal so the wrapper does not reconnect-war for a tunnel another host claimed.
    /// </summary>
    private void OnConnectionStatusChanged(object? sender, ConnectionStatusChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Status != ConnectionStatus.Disconnected)
        {
            return;
        }

        if (this.shuttingDown)
        {
            return;
        }

        if (Interlocked.Exchange(ref this.disconnectRaised, 1) != 0)
        {
            return;
        }

        var tooManyConnections = this.host.DisconnectReason == SshDisconnectReason.TooManyConnections;
        var message = e.DisconnectException?.Message ?? this.host.DisconnectException?.Message;
        this.Disconnected?.Invoke(this, new RelayHostDisconnectInfo(tooManyConnections, message));
    }

    /// <summary>
    /// Handles the SDK relay host's <c>ForwardedPortConnecting</c> event (issue #1350). Once teardown
    /// has begun we reject the connection so the SDK never starts a fresh local
    /// <see cref="System.Net.Sockets.TcpClient"/> connect that would be aborted mid-flight and leaked as
    /// an unobserved fault; otherwise we observe any transform pipeline so its terminal shutdown outcome
    /// is consumed at the site that owns the relay host.
    /// </summary>
    private void OnForwardedPortConnecting(object? sender, ForwardedPortConnectingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (this.shuttingDown)
        {
            // A null transformed stream tells the SDK to reject the connection instead of connecting.
            e.TransformTask = Task.FromResult<Stream?>(null);
            return;
        }

        TunnelRelayDevTunnelHost.ObserveForwardedConnectTransform(e.TransformTask);
    }
}
