using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DevTunnels.Connections;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Tcp.Events;

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
    private volatile bool shuttingDown;

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

        // Issue #1350: the SDK bridges each incoming relay channel to the local port with a
        // fire-and-forget TcpClient connect. On teardown the SDK aborts that in-flight connect
        // (SocketError.OperationAborted, 995); on the loopback dual-stack path the SDK abandons the
        // faulted connect Task (it is not returned by DisposeAsync), so it reaches the finalizer as an
        // unobserved fault and is escalated to a crash dialog. Subscribing to ForwardedPortConnecting
        // is the seam we own: once teardown has begun we reject new forwarded connections so the SDK
        // never starts a fresh connect that would be aborted, and we observe any in-flight transform
        // so its terminal shutdown outcome is consumed at the source rather than on the finalizer.
        host.ForwardedPortConnecting += this.OnForwardedPortConnecting;
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

            // Reject any further forwarded-port connections before we start cancelling/disposing, so
            // no new SDK local TcpClient connect can begin racing the teardown (issue #1350).
            this.shuttingDown = true;
            host.ForwardedPortConnecting -= this.OnForwardedPortConnecting;
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
    /// Handles the SDK relay host's <c>ForwardedPortConnecting</c> event — the seam that fires as the
    /// SDK is about to bridge an incoming relay channel to the local port (issue #1350). Once teardown
    /// has begun (<see cref="shuttingDown"/>) we reject the connection so the SDK never starts a fresh
    /// local <see cref="System.Net.Sockets.TcpClient"/> connect that would be aborted mid-flight and
    /// leaked as an unobserved fault; otherwise we observe any transform pipeline the SDK is running so
    /// its terminal shutdown outcome is consumed at the site that owns the relay host.
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

        ObserveForwardedConnectTransform(e.TransformTask);
    }

    /// <summary>
    /// Observes a forwarded-port transform task (if any) so a terminal shutdown-race outcome the SDK
    /// surfaces through it is consumed here — at the site that owns the relay host — instead of being
    /// abandoned to <see cref="TaskScheduler.UnobservedTaskException"/>. Genuine (non-shutdown) faults
    /// are re-surfaced so real defects are never silently swallowed. Extracted as an internal seam so
    /// the observe-and-classify behaviour is regressible without a live SDK relay host.
    /// </summary>
    internal static void ObserveForwardedConnectTransform(Task? transformTask)
    {
        if (transformTask is null)
        {
            return;
        }

        _ = transformTask.ContinueWith(
            static completed =>
            {
                var exception = completed.Exception;
                if (exception is not null && !IsExpectedShutdownException(exception))
                {
                    // Genuine defect surfacing through the transform pipeline — re-raise so it is not
                    // silently swallowed (it becomes a fresh fault that reaches the normal handlers).
                    throw exception;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
