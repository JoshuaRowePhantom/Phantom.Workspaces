using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Owns the full lifecycle of a Workspaces-owned dev tunnel: ensures the tunnel exists, enforces the
/// single forwarded-port invariant for the GUI's current listening port, applies the configured access
/// mode, hosts the relay, and publishes the public access point and live status. The Dev Tunnels SDK
/// types are confined to the concrete implementation; this interface is fakeable in tests.
/// </summary>
public interface IDevTunnelHostService : IAsyncDisposable
{
    /// <summary>The current host status snapshot.</summary>
    DevTunnelHostStatus Status { get; }

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    event EventHandler<DevTunnelHostStatus>? StatusChanged;

    /// <summary>
    /// Ensures the tunnel exists, forwards the supplied local port (enforcing the single-port
    /// invariant), applies the configured access mode, and begins hosting.
    /// </summary>
    Task StartAsync(int localPort, string protocol, DevTunnelConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a configuration change (e.g. access mode or listen port) without losing the tunnel
    /// identity.
    /// </summary>
    Task ReconfigureAsync(int localPort, string protocol, DevTunnelConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>Stops hosting and releases the relay host; the tunnel resource persists.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
