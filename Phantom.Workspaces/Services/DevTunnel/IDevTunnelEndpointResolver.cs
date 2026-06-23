using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// The resolved relay endpoint for a tunnel located by name.
/// </summary>
/// <param name="BaseUri">The relay base URI of the tunnel's single forwarded port.</param>
/// <param name="TunnelAuthToken">
/// The pre-shared <c>X-Tunnel-Authorization</c> token to send, when in Token access mode; null for
/// Private access (where identity-based authorization is used) and Anonymous access.
/// </param>
public sealed record DevTunnelEndpointResolution(Uri BaseUri, string? TunnelAuthToken);

/// <summary>
/// Resolves a dev tunnel's live relay endpoint from just its name, relying on the host's single
/// forwarded-port invariant to discover the port automatically. Backed by the Dev Tunnels management
/// client and the unified <see cref="IDevTunnelAuthTokenProvider"/>; fakeable in tests.
/// </summary>
public interface IDevTunnelEndpointResolver
{
    /// <summary>
    /// Looks up the tunnel by name, reads its single forwarded port, and constructs the relay endpoint.
    /// </summary>
    Task<DevTunnelEndpointResolution> ResolveAsync(
        string tunnelName,
        DevTunnelAccessMode accessMode,
        CancellationToken cancellationToken = default);
}
