using System;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Watches the relay HOST connection and, on an unexpected SDK disconnect, re-runs the full connect
/// sequence (fetch a connect-ready tunnel, create a new SDK host, subscribe events, connect) with
/// bounded exponential backoff and jitter — without restarting the application (issue #1375). Mirrors
/// the client-side <see cref="DevTunnelConnectionMonitor"/>: all delays go through an injected
/// <see cref="IDelayScheduler"/> and jitter is injectable, so reconnection is fully deterministic in
/// tests (no real timers).
/// </summary>
/// <remarks>
/// The <c>connect</c> delegate establishes a fresh relay-host session and throws on failure. A
/// <c>TooManyConnections</c> disconnect is treated as terminal (another host claimed the tunnel): the
/// monitor surfaces <see cref="DevTunnelConnectionState.Failed"/> and does NOT reconnect, so we never
/// fight another host for the tunnel (the SDK's own <c>ConnectAsync</c> throws in that case anyway).
/// </remarks>
public sealed class DevTunnelHostConnectionMonitor
{
    private readonly Func<CancellationToken, Task> connect;
    private readonly IDelayScheduler delayScheduler;
    private readonly DevTunnelReconnectOptions options;
    private readonly Func<double> nextJitterSample;

    public DevTunnelHostConnectionMonitor(
        Func<CancellationToken, Task> connect,
        IDelayScheduler delayScheduler,
        DevTunnelReconnectOptions? options = null,
        Func<double>? nextJitterSample = null)
    {
        this.connect = connect ?? throw new ArgumentNullException(nameof(connect));
        this.delayScheduler = delayScheduler ?? throw new ArgumentNullException(nameof(delayScheduler));
        this.options = options ?? DevTunnelReconnectOptions.Default;
        this.nextJitterSample = nextJitterSample ?? (static () => Random.Shared.NextDouble());
    }

    /// <summary>The current relay-host connection state.</summary>
    public DevTunnelConnectionState State { get; private set; } = DevTunnelConnectionState.Connected;

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    public event EventHandler<DevTunnelConnectionState>? StateChanged;

    /// <summary>
    /// Handles a reported relay-host disconnect. When <paramref name="tooManyConnections"/> is true the
    /// disconnect is terminal — surface <see cref="DevTunnelConnectionState.Failed"/> without reconnecting.
    /// Otherwise enter <see cref="DevTunnelConnectionState.Reconnecting"/> and retry the full connect
    /// sequence with bounded exponential backoff and jitter until it succeeds, the attempt budget is
    /// exhausted (<see cref="DevTunnelConnectionState.Failed"/>), or cancellation.
    /// </summary>
    public async Task HandleDisconnectAsync(bool tooManyConnections, CancellationToken cancellationToken = default)
    {
        if (tooManyConnections)
        {
            // Another host claimed the tunnel — do not reconnect-war; surface a terminal failure.
            this.SetState(DevTunnelConnectionState.Failed);
            return;
        }

        this.SetState(DevTunnelConnectionState.Reconnecting);

        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (this.options.MaxAttempts is int maxAttempts && attempt >= maxAttempts)
            {
                this.SetState(DevTunnelConnectionState.Failed);
                return;
            }

            await this.delayScheduler.DelayAsync(this.ComputeDelay(attempt), cancellationToken).ConfigureAwait(false);

            try
            {
                await this.connect(cancellationToken).ConfigureAwait(false);
                this.SetState(DevTunnelConnectionState.Connected);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                attempt++;
                this.SetState(DevTunnelConnectionState.Reconnecting);
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

    private void SetState(DevTunnelConnectionState state)
    {
        this.State = state;
        this.StateChanged?.Invoke(this, state);
    }
}
