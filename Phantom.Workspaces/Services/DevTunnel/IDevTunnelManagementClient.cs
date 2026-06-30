using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Identifies a Workspaces-owned tunnel after it has been ensured/created.
/// </summary>
/// <param name="TunnelId">The management-plane tunnel id.</param>
/// <param name="TunnelName">The tunnel name.</param>
public sealed record DevTunnelDescriptor(string TunnelId, string TunnelName);

/// <summary>
/// Domain-level management operations the <see cref="IDevTunnelHostService"/> needs, expressed without
/// any Dev Tunnels SDK types so the orchestration can be unit-tested with a fake. The concrete
/// implementation wraps <c>Microsoft.DevTunnels.Management.TunnelManagementClient</c>.
/// </summary>
public interface IDevTunnelManagementClient
{
    /// <summary>
    /// Gets the tunnel identified by <paramref name="tunnelId"/>/<paramref name="tunnelName"/> when
    /// present, otherwise creates a persistent tunnel and returns its descriptor.
    /// </summary>
    Task<DevTunnelDescriptor> EnsureTunnelAsync(
        string? tunnelId,
        string? tunnelName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures exactly one forwarded port exists on the tunnel for <paramref name="localPort"/> using
    /// the given protocol, removing any previously-forwarded ports that no longer match (single-port
    /// invariant).
    /// </summary>
    Task SetSingleForwardedPortAsync(
        string tunnelId,
        int localPort,
        string protocol,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the access control matching <paramref name="accessMode"/> to the tunnel and returns
    /// the connect-scope tunnel token for non-Anonymous modes (null for Anonymous mode). The token is
    /// fetched from the Management API after the access-control update; it is short-lived and should
    /// not be persisted.
    /// </summary>
    Task<string?> ApplyAccessModeAsync(
        string tunnelId,
        DevTunnelAccessMode accessMode,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the public https access-point URL for the hosted port on the tunnel.</summary>
    Task<string> GetAccessPointUrlAsync(
        string tunnelId,
        int localPort,
        CancellationToken cancellationToken = default);
}
