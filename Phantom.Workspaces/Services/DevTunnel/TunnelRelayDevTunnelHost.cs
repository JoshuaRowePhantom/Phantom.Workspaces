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
    private readonly Func<string, int, CancellationToken, Task<IRelayHostSession>> connectSessionAsync;
    private readonly IDelayScheduler delayScheduler;
    private readonly DevTunnelReconnectOptions reconnectOptions;
    private readonly Func<double>? nextJitterSample;

    private IRelayHostSession? currentSession;
    private CancellationTokenSource? lifetimeCts;
    private DevTunnelHostConnectionMonitor? monitor;
    private string? tunnelId;
    private int localPort;
    private volatile bool shuttingDown;
    private volatile bool connected;

    public TunnelRelayDevTunnelHost(DevTunnelManagementClientWrapper managementClientWrapper)
    {
        this.managementClientWrapper = managementClientWrapper ?? throw new ArgumentNullException(nameof(managementClientWrapper));
        this.connectSessionAsync = this.ConnectSdkSessionAsync;
        this.delayScheduler = RealDelayScheduler.Instance;
        this.reconnectOptions = DevTunnelReconnectOptions.Default;
        this.nextJitterSample = null;
    }

    /// <summary>
    /// Test seam (issue #1375): injects the session connector, delay scheduler, and backoff/jitter so
    /// the disconnect-detection and reconnect loop are regressible without a live SDK relay host.
    /// </summary>
    internal TunnelRelayDevTunnelHost(
        Func<string, int, CancellationToken, Task<IRelayHostSession>> connectSessionAsync,
        IDelayScheduler delayScheduler,
        DevTunnelReconnectOptions reconnectOptions,
        Func<double>? nextJitterSample = null)
    {
        this.managementClientWrapper = null!;
        this.connectSessionAsync = connectSessionAsync ?? throw new ArgumentNullException(nameof(connectSessionAsync));
        this.delayScheduler = delayScheduler ?? throw new ArgumentNullException(nameof(delayScheduler));
        this.reconnectOptions = reconnectOptions ?? throw new ArgumentNullException(nameof(reconnectOptions));
        this.nextJitterSample = nextJitterSample;
    }

    /// <inheritdoc />
    public event EventHandler<DevTunnelConnectionState>? ConnectionStateChanged;

    /// <inheritdoc />
    public bool IsRunning => this.connected;

    /// <summary>
    /// The in-flight reconnect task, if a reconnect is currently being driven by a reported disconnect.
    /// Exposed for tests to deterministically await the reconnect sequence (issue #1375).
    /// </summary>
    internal Task? ReconnectTask { get; private set; }

    public async Task StartAsync(string tunnelId, int localPort, CancellationToken cancellationToken = default)
    {
        this.shuttingDown = false;
        this.tunnelId = tunnelId;
        this.localPort = localPort;

        // Reconnects run under a lifetime token (linked to the caller's token) so StopAsync can cancel
        // an in-flight backoff/reconnect. The initial connect honours the caller's token via this link.
        this.lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        this.monitor = new DevTunnelHostConnectionMonitor(
            connect: ct => this.ConnectAndTrackSessionAsync(ct),
            delayScheduler: this.delayScheduler,
            options: this.reconnectOptions,
            nextJitterSample: this.nextJitterSample);
        this.monitor.StateChanged += this.OnMonitorStateChanged;

        await this.ConnectAndTrackSessionAsync(this.lifetimeCts.Token).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // Reject any further forwarded-port connections / disconnect-driven reconnects before we start
        // cancelling/disposing, so no new SDK work can begin racing the teardown (issues #1350, #1375).
        this.shuttingDown = true;
        this.connected = false;

        var session = this.currentSession;
        var cts = this.lifetimeCts;
        var reconnect = this.ReconnectTask;
        var currentMonitor = this.monitor;
        this.currentSession = null;
        this.lifetimeCts = null;
        this.monitor = null;
        this.ReconnectTask = null;

        if (currentMonitor is not null)
        {
            currentMonitor.StateChanged -= this.OnMonitorStateChanged;
        }

        // Cancel the reconnect loop first so an in-flight backoff/reconnect unwinds as cancelled.
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by a concurrent/prior teardown — nothing to cancel.
            }
        }

        if (reconnect is not null)
        {
            try
            {
                await reconnect.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected — the reconnect loop observed the lifetime-token cancellation.
            }
            catch (Exception exception) when (IsExpectedShutdownException(exception))
            {
                // Terminal shutdown outcome of the SDK's fire-and-forget work — consume.
            }
        }

        if (session is not null)
        {
            session.Disconnected -= this.OnSessionDisconnected;
            await DisposeRelayHostSafelyAsync(session).ConfigureAwait(false);
        }

        cts?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await this.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the full connect sequence and tracks the resulting session: fetch a connect-ready tunnel,
    /// create a new SDK relay host, subscribe to its status/forwarded-port events, and connect. On
    /// success the session becomes the current session, its disconnect signal is wired to
    /// <see cref="OnSessionDisconnected"/>, and <see cref="IsRunning"/> becomes true. Used both for the
    /// initial connect and as the reconnect monitor's connect delegate (issue #1375).
    /// </summary>
    private async Task ConnectAndTrackSessionAsync(CancellationToken cancellationToken)
    {
        var session = await this.connectSessionAsync(this.tunnelId!, this.localPort, cancellationToken).ConfigureAwait(false);
        session.Disconnected += this.OnSessionDisconnected;
        this.currentSession = session;
        this.connected = true;
    }

    /// <summary>
    /// Handles the SDK-reported terminal disconnect of the current relay-host session (issue #1375).
    /// Marks the relay dead (so <see cref="IsRunning"/> reflects reality), then disposes the dead
    /// session and drives the reconnect monitor. Ignored during our own teardown or for a stale session.
    /// </summary>
    private void OnSessionDisconnected(object? sender, RelayHostDisconnectInfo info)
    {
        if (this.shuttingDown)
        {
            return;
        }

        if (!ReferenceEquals(sender, this.currentSession))
        {
            // A disconnect from an already-replaced session — ignore.
            return;
        }

        this.connected = false;
        var dead = this.currentSession;
        this.currentSession = null;
        if (dead is not null)
        {
            dead.Disconnected -= this.OnSessionDisconnected;
        }

        var reconnectToken = this.lifetimeCts?.Token ?? CancellationToken.None;
        this.ReconnectTask = this.ReconnectAsync(dead, info.TooManyConnections, reconnectToken);
    }

    private async Task ReconnectAsync(IRelayHostSession? dead, bool tooManyConnections, CancellationToken cancellationToken)
    {
        if (dead is not null)
        {
            // Dispose the dead host safely (consuming the SDK's terminal shutdown outcomes) before we
            // recreate — issues #1301/#1322/#1350 handling lives in the session's DisposeAsync.
            await DisposeRelayHostSafelyAsync(dead).ConfigureAwait(false);
        }

        var currentMonitor = this.monitor;
        if (currentMonitor is not null)
        {
            await currentMonitor.HandleDisconnectAsync(tooManyConnections, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnMonitorStateChanged(object? sender, DevTunnelConnectionState state)
    {
        this.ConnectionStateChanged?.Invoke(this, state);
    }

    /// <summary>
    /// Production session connector: fetches a fresh, connect-ready tunnel (its forwarded ports are
    /// required by the SDK relay host; the cached tunnel's ports were cleared by the access-control
    /// update), creates the SDK <see cref="TunnelRelayTunnelHost"/>, and connects it inside an
    /// <see cref="SdkRelayHostSession"/> adapter that owns the #1322 shutdown-token ordering, the #1350
    /// forwarded-port handling, and the #1375 connection-status disconnect detection.
    /// </summary>
    private async Task<IRelayHostSession> ConnectSdkSessionAsync(string tunnelId, int localPort, CancellationToken cancellationToken)
    {
        var tunnel = await this.managementClientWrapper
            .GetConnectReadyTunnelAsync(tunnelId, cancellationToken)
            .ConfigureAwait(false);

        var sdkHost = new TunnelRelayTunnelHost(
            this.managementClientWrapper.ManagementClient,
            new TraceSource(nameof(TunnelRelayDevTunnelHost)));

        var session = new SdkRelayHostSession(sdkHost, cancellationToken);
        await session.ConnectAsync(tunnel, cancellationToken).ConfigureAwait(false);
        return session;
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
