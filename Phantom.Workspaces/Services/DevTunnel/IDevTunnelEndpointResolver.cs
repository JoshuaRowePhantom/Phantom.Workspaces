using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// The resolved relay endpoint for a tunnel located by name.
/// </summary>
/// <param name="BaseUri">The relay base URI of the tunnel's single forwarded port.</param>
public sealed record DevTunnelEndpointResolution(Uri BaseUri)
{
    private readonly Func<CancellationToken, ValueTask>? invalidate;

    internal DevTunnelEndpointResolution(Uri baseUri, Func<CancellationToken, ValueTask>? invalidate)
        : this(baseUri) => this.invalidate = invalidate;

    internal ValueTask InvalidateForRefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return this.invalidate?.Invoke(cancellationToken) ?? ValueTask.CompletedTask;
    }
}

/// <summary>
/// Resolves a dev tunnel's live relay endpoint from just its name, relying on the host's single
/// forwarded-port invariant to discover the port automatically. Backed by the Dev Tunnels management
/// broker; fakeable in tests. The result contains metadata only, never credentials.
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
