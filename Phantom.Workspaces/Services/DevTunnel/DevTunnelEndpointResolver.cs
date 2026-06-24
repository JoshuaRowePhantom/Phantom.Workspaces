using System;
using System.Linq;
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
    private readonly string? accessTokenSource;
    private readonly Func<string, string?> tokenSourceResolver;

    public DevTunnelEndpointResolver(
        IDevTunnelLookupClient lookupClient,
        string? accessTokenSource = null,
        Func<string, string?>? tokenSourceResolver = null)
    {
        this.lookupClient = lookupClient ?? throw new ArgumentNullException(nameof(lookupClient));
        this.accessTokenSource = accessTokenSource;
        this.tokenSourceResolver = tokenSourceResolver
            ?? (static sourceName => Environment.GetEnvironmentVariable(sourceName));
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
        var tunnelAuthToken = this.ResolveTunnelAuthToken(accessMode);
        return new DevTunnelEndpointResolution(baseUri, tunnelAuthToken);
    }

    private string? ResolveTunnelAuthToken(DevTunnelAccessMode accessMode)
    {
        if (accessMode != DevTunnelAccessMode.Token)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(this.accessTokenSource))
        {
            throw new InvalidOperationException(
                "Token access mode requires a configured AccessTokenSource to resolve the tunnel access token.");
        }

        var token = this.tokenSourceResolver(this.accessTokenSource);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"The dev tunnel access token source '{this.accessTokenSource}' did not yield a token.");
        }

        return token;
    }
}
