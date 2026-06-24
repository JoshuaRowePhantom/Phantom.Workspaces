using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Lifecycle state of the <see cref="IDevTunnelHostService"/>.
/// </summary>
public enum DevTunnelHostState
{
    /// <summary>No tunnel is being hosted.</summary>
    Stopped,

    /// <summary>The tunnel is being ensured/created and hosting is starting.</summary>
    Starting,

    /// <summary>The tunnel is hosted and forwarding the local port.</summary>
    Hosting,

    /// <summary>Hosting was interrupted and is being re-established.</summary>
    Reconnecting,

    /// <summary>Hosting failed; see <see cref="DevTunnelHostStatus.LastError"/>.</summary>
    Error,
}

/// <summary>
/// Immutable snapshot of the dev-tunnel host's current state, surfaced to the network status display
/// and the global status dropdown.
/// </summary>
/// <param name="State">The current lifecycle state.</param>
/// <param name="AccessPointUrl">The public https URL of the hosted port, when <see cref="DevTunnelHostState.Hosting"/>; otherwise null.</param>
/// <param name="TunnelId">The id of the owned tunnel, when known.</param>
/// <param name="AccessMode">The access mode the tunnel currently requires of inbound clients.</param>
/// <param name="LastError">The most recent error message, when <see cref="DevTunnelHostState.Error"/>; otherwise null.</param>
public sealed record DevTunnelHostStatus(
    DevTunnelHostState State,
    string? AccessPointUrl,
    string? TunnelId,
    DevTunnelAccessMode AccessMode,
    string? LastError)
{
    /// <summary>The default stopped status.</summary>
    public static DevTunnelHostStatus Stopped { get; } =
        new(DevTunnelHostState.Stopped, AccessPointUrl: null, TunnelId: null, DevTunnelAccessMode.Private, LastError: null);
}
