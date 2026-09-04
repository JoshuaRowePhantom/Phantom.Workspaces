using System;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Abstraction over a single established Dev Tunnels SDK relay-host session so the reconnect logic in
/// <see cref="TunnelRelayDevTunnelHost"/> can be exercised without a live SDK relay host. The concrete
/// adapter (<c>SdkRelayHostSession</c>) wraps <c>Microsoft.DevTunnels.Connections.TunnelRelayTunnelHost</c>
/// and raises <see cref="Disconnected"/> when the SDK reports the terminal
/// <c>ConnectionStatus.Disconnected</c> transition (issue #1375).
/// </summary>
internal interface IRelayHostSession : IAsyncDisposable
{
    /// <summary>
    /// Raised once when the SDK reports the session has terminally disconnected for a reason other than
    /// our own teardown. Never raised while the session is being disposed by us.
    /// </summary>
    event EventHandler<RelayHostDisconnectInfo>? Disconnected;
}

/// <summary>
/// Describes an SDK relay-host session disconnect so the reconnect monitor can decide whether to
/// reconnect (transient/unknown) or surface a terminal failure (another host claimed the tunnel).
/// </summary>
/// <param name="TooManyConnections">
/// <see langword="true"/> when the SDK reported <c>SshDisconnectReason.TooManyConnections</c> — another
/// host connected for the same tunnel, so reconnecting would start a reconnect-war; surface Failed instead.
/// </param>
/// <param name="ErrorMessage">The disconnect exception message, when the SDK provided one; otherwise null.</param>
internal readonly record struct RelayHostDisconnectInfo(bool TooManyConnections, string? ErrorMessage);
