using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// The facts about a tunnel needed to construct its relay endpoint, expressed without any Dev Tunnels
/// SDK types so endpoint resolution can be unit-tested with a fake. The concrete implementation reads
/// these from <c>Microsoft.DevTunnels.Management.TunnelManagementClient</c>.
/// </summary>
/// <param name="TunnelId">The management-plane tunnel id.</param>
/// <param name="ClusterId">The cluster the tunnel lives in (part of the relay host name).</param>
/// <param name="ForwardedPorts">The port numbers currently forwarded by the tunnel.</param>
public sealed record DevTunnelLookupResult(
    string TunnelId,
    string ClusterId,
    IReadOnlyList<int> ForwardedPorts,
    string? ConnectToken = null);

/// <summary>
/// Looks up a tunnel by name on the management plane, returning the facts needed to build its relay
/// endpoint. SDK-free seam, fakeable in tests; the concrete implementation wraps the SDK management
/// client and the unified <see cref="IDevTunnelAuthTokenProvider"/> for identity.
/// </summary>
public interface IDevTunnelLookupClient
{
    /// <summary>Looks up the tunnel identified by <paramref name="tunnelName"/>.</summary>
    Task<DevTunnelLookupResult> LookupByNameAsync(string tunnelName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discovers the single Workspaces-owned tunnel (by the marker label), for "auto" connection when no
    /// tunnel name is configured. Throws when none, or when more than one, is found.
    /// </summary>
    Task<DevTunnelLookupResult> DiscoverSingleAsync(CancellationToken cancellationToken = default);
}
