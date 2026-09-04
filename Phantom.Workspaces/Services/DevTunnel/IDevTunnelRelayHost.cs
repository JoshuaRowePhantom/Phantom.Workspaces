using System;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Thin wrapper over <c>Microsoft.DevTunnels.Connections.TunnelRelayTunnelHost</c> so the in-process
/// relay host can be faked in tests. Starts accepting relay traffic for a tunnel and forwarding it to
/// the local listening port.
/// </summary>
public interface IDevTunnelRelayHost : IAsyncDisposable
{
    /// <summary>
    /// Whether the relay host currently has an established SDK connection. Reflects the SDK
    /// <c>ConnectionStatus</c> (false once the SDK reports <c>Disconnected</c>), not merely whether a
    /// host object was ever created (issue #1375).
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Raised when the relay host's underlying SDK connection state changes — <c>Reconnecting</c> after
    /// an unexpected disconnect, <c>Connected</c> once re-established, or <c>Failed</c> when reconnection
    /// is abandoned (issue #1375). Lets the host service surface the real state instead of appearing
    /// healthy while remote hosting is down.
    /// </summary>
    event EventHandler<DevTunnelConnectionState>? ConnectionStateChanged;

    /// <summary>Starts hosting the tunnel, forwarding relay connections to <c>127.0.0.1:<paramref name="localPort"/></c>.</summary>
    Task StartAsync(string tunnelId, int localPort, CancellationToken cancellationToken = default);

    /// <summary>Stops hosting and releases the relay connection.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
