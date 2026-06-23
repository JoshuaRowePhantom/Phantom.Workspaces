using System;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// The reconnection state of a client tunnel connection, surfaced to the workspace connection status.
/// </summary>
public enum DevTunnelConnectionState
{
    /// <summary>The connection is established and healthy.</summary>
    Connected,

    /// <summary>The connection dropped and is being re-established (re-resolve + backoff retries).</summary>
    Reconnecting,

    /// <summary>Reconnection was abandoned (bounded attempts exhausted or cancelled).</summary>
    Failed,
}

/// <summary>
/// Immutable snapshot of a client tunnel connection's health.
/// </summary>
/// <param name="State">The current connection state.</param>
/// <param name="CurrentBaseUri">The relay base URI currently in use, when known.</param>
/// <param name="LastError">The most recent failure message, when reconnecting/failed; otherwise null.</param>
public sealed record DevTunnelConnectionStatus(
    DevTunnelConnectionState State,
    Uri? CurrentBaseUri,
    string? LastError);

/// <summary>
/// Backoff policy for reconnect attempts.
/// </summary>
/// <param name="BaseDelay">The delay before the first retry; doubled each subsequent attempt.</param>
/// <param name="MaxDelay">The delay cap.</param>
/// <param name="MaxAttempts">The maximum number of retries before giving up; null means unbounded.</param>
/// <param name="JitterFraction">
/// The fraction of the computed delay added as random jitter (0 disables jitter). For example, 0.2
/// adds up to 20% of the delay.
/// </param>
public sealed record DevTunnelReconnectOptions(
    TimeSpan BaseDelay,
    TimeSpan MaxDelay,
    int? MaxAttempts = null,
    double JitterFraction = 0.2)
{
    /// <summary>The default policy: 1s base, 30s cap, unbounded, 20% jitter.</summary>
    public static DevTunnelReconnectOptions Default { get; } =
        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
}
