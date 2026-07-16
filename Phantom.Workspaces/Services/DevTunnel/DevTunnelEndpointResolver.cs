using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Resolves a dev tunnel's live relay endpoint from just its name, discovering the forwarded port
/// automatically (relying on the host's single forwarded-port invariant) and deciding the
/// <c>X-Tunnel-Authorization</c> token from the access mode. Backed by an SDK-free
/// <see cref="IDevTunnelLookupClient"/> so the URI-building and token logic are unit-testable.
/// </summary>
public sealed class DevTunnelEndpointResolver : IDevTunnelEndpointResolver
{
    private readonly IDevTunnelLookupClient lookupClient;

    public DevTunnelEndpointResolver(IDevTunnelLookupClient lookupClient)
    {
        this.lookupClient = lookupClient ?? throw new ArgumentNullException(nameof(lookupClient));
    }

    public async Task<DevTunnelEndpointResolution> ResolveAsync(
        string tunnelName,
        DevTunnelAccessMode accessMode,
        CancellationToken cancellationToken = default)
    {
        // An "auto" (or blank) name discovers the single Workspaces-owned tunnel by its marker label;
        // otherwise the tunnel is located by its name label.
        var isAuto = DevTunnelNaming.IsAuto(tunnelName);
        var lookup = isAuto
            ? await this.lookupClient.DiscoverSingleAsync(cancellationToken).ConfigureAwait(false)
            : await this.lookupClient.LookupByNameAsync(tunnelName, cancellationToken).ConfigureAwait(false);

        var tunnelDescription = isAuto ? "auto-discovered dev tunnel" : $"dev tunnel '{tunnelName}'";
        if (lookup.ForwardedPorts.Count != 1)
        {
            throw new InvalidOperationException(
                $"The {tunnelDescription} must forward exactly one port to be resolved, but forwards {lookup.ForwardedPorts.Count}.");
        }

        var port = lookup.ForwardedPorts[0];
        var baseUri = new Uri($"https://{lookup.TunnelId}-{port}.{lookup.ClusterId}.devtunnels.ms/");
        // Private connect is identity-derived (design #19): the client authorizes with its GitHub
        // identity, so a null Connect token is expected and valid — the resolver must not throw.
        // The retired Token mode still requires a pre-shared Connect token.
        var tunnelAuthToken = accessMode switch
        {
            DevTunnelAccessMode.Anonymous => null,
            DevTunnelAccessMode.Private => lookup.ConnectToken,
#pragma warning disable CS0618 // Token access mode is obsolete; retained for migration.
            DevTunnelAccessMode.Token => lookup.ConnectToken
                ?? throw new InvalidOperationException(
                    "The Management API did not return a Connect-scope tunnel token. " +
                    "Ensure the GitHub identity has access to the tunnel."),
#pragma warning restore CS0618
            _ => lookup.ConnectToken,
        };
        return new DevTunnelEndpointResolution(baseUri, tunnelAuthToken);
    }
}
