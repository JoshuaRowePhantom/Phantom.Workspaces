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
        // Both Private and Token modes require X-Tunnel-Authorization: tunnel <connect-token>.
        // The connect token is fetched automatically by the Management API when the lookup request
        // includes TokenScopes=[Connect] — no env-var or manual token configuration needed.
        var tunnelAuthToken = accessMode switch
        {
            DevTunnelAccessMode.Anonymous => null,
            _ => lookup.ConnectToken
                ?? throw new InvalidOperationException(
                    "The Management API did not return a Connect-scope tunnel token. " +
                    "Ensure the GitHub identity has access to the tunnel."),
        };
        return new DevTunnelEndpointResolution(baseUri, tunnelAuthToken);
    }
}
