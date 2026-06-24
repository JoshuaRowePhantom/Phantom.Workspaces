using System;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Watches a client tunnel connection and, on failure, re-resolves the endpoint and reconnects with
/// bounded exponential backoff and jitter — without restarting the workspace. Re-resolution is
/// event-driven (triggered by reported failures), so a healthy connection performs no extra
/// management calls. All delays go through an injected <see cref="IDelayScheduler"/> and jitter is
/// injectable, so reconnection is fully deterministic in tests (no real timers).
/// </summary>
/// <remarks>
/// The endpoint is produced by an injected <c>resolveEndpoint</c> delegate: for tunnel-name mode this
/// calls <see cref="IDevTunnelEndpointResolver"/> (re-resolving each reconnect, picking up a changed
/// port); for explicit-access-point mode it returns the same fixed endpoint without any management
/// lookup. The <c>connect</c> delegate establishes/validates the connection and throws on failure.
/// </remarks>
public sealed class DevTunnelConnectionMonitor
{
    private readonly Func<CancellationToken, Task<DevTunnelEndpointResolution>> resolveEndpoint;
    private readonly Func<DevTunnelEndpointResolution, CancellationToken, Task> connect;
    private readonly IDelayScheduler delayScheduler;
    private readonly DevTunnelReconnectOptions options;
    private readonly Func<double> nextJitterSample;

    public DevTunnelConnectionMonitor(
        Func<CancellationToken, Task<DevTunnelEndpointResolution>> resolveEndpoint,
        Func<DevTunnelEndpointResolution, CancellationToken, Task> connect,
        IDelayScheduler delayScheduler,
        DevTunnelReconnectOptions? options = null,
        Func<double>? nextJitterSample = null)
    {
        this.resolveEndpoint = resolveEndpoint ?? throw new ArgumentNullException(nameof(resolveEndpoint));
        this.connect = connect ?? throw new ArgumentNullException(nameof(connect));
        this.delayScheduler = delayScheduler ?? throw new ArgumentNullException(nameof(delayScheduler));
        this.options = options ?? DevTunnelReconnectOptions.Default;
        this.nextJitterSample = nextJitterSample ?? (static () => Random.Shared.NextDouble());
    }

    /// <summary>The current connection status.</summary>
    public DevTunnelConnectionStatus Status { get; private set; } =
        new(DevTunnelConnectionState.Reconnecting, CurrentBaseUri: null, LastError: null);

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    public event EventHandler<DevTunnelConnectionStatus>? StatusChanged;

    /// <summary>
    /// Establishes the initial connection (resolve + connect). Throws if the first connection fails;
    /// transient reconnection is handled by <see cref="HandleConnectionFailedAsync"/>.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var resolution = await this.resolveEndpoint(cancellationToken).ConfigureAwait(false);
        await this.connect(resolution, cancellationToken).ConfigureAwait(false);
        this.SetStatus(new DevTunnelConnectionStatus(DevTunnelConnectionState.Connected, resolution.BaseUri, LastError: null));
    }

    /// <summary>
    /// Handles a reported connection failure: enters <see cref="DevTunnelConnectionState.Reconnecting"/>
    /// and retries (re-resolve + connect) with bounded exponential backoff and jitter until it succeeds,
    /// the attempt budget is exhausted (<see cref="DevTunnelConnectionState.Failed"/>), or cancellation.
    /// </summary>
    public async Task HandleConnectionFailedAsync(Exception failure, CancellationToken cancellationToken = default)
    {
        this.SetStatus(new DevTunnelConnectionStatus(
            DevTunnelConnectionState.Reconnecting,
            this.Status.CurrentBaseUri,
            failure.Message));

        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (this.options.MaxAttempts is int maxAttempts && attempt >= maxAttempts)
            {
                this.SetStatus(new DevTunnelConnectionStatus(
                    DevTunnelConnectionState.Failed,
                    this.Status.CurrentBaseUri,
                    failure.Message));
                return;
            }

            await this.delayScheduler.DelayAsync(this.ComputeDelay(attempt), cancellationToken).ConfigureAwait(false);

            try
            {
                var resolution = await this.resolveEndpoint(cancellationToken).ConfigureAwait(false);
                await this.connect(resolution, cancellationToken).ConfigureAwait(false);
                this.SetStatus(new DevTunnelConnectionStatus(DevTunnelConnectionState.Connected, resolution.BaseUri, LastError: null));
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failure = exception;
                this.SetStatus(new DevTunnelConnectionStatus(
                    DevTunnelConnectionState.Reconnecting,
                    this.Status.CurrentBaseUri,
                    exception.Message));
                attempt++;
            }
        }
    }

    private TimeSpan ComputeDelay(int attempt)
    {
        var exponentialTicks = this.options.BaseDelay.Ticks * Math.Pow(2, attempt);
        var cappedTicks = Math.Min(exponentialTicks, this.options.MaxDelay.Ticks);
        var jitterTicks = cappedTicks * this.options.JitterFraction * this.nextJitterSample();
        return TimeSpan.FromTicks((long)(cappedTicks + jitterTicks));
    }

    private void SetStatus(DevTunnelConnectionStatus status)
    {
        this.Status = status;
        this.StatusChanged?.Invoke(this, status);
    }
}
