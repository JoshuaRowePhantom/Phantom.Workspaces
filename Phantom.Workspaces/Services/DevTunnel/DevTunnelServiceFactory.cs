using System;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Composition helper that builds the Dev Tunnels SDK <see cref="ITunnelManagementClient"/> and the
/// service objects that compose it (host service, endpoint resolver), wired to the unified
/// <see cref="IDevTunnelAuthTokenProvider"/> for management identity. The SDK access-token callback
/// supplies the GitHub-scheme authentication header from the signed-in identity. The concrete SDK
/// wrapper types stay internal behind the returned interfaces.
/// </summary>
public sealed class DevTunnelServiceFactory
{
    private static readonly ProductInfoHeaderValue UserAgent = new("Phantom.Workspaces", "1.0");

    private readonly IDevTunnelAuthTokenProvider authTokenProvider;

    public DevTunnelServiceFactory(IDevTunnelAuthTokenProvider? authTokenProvider = null)
    {
        this.authTokenProvider = authTokenProvider ?? new GitHubDevTunnelAuthTokenProvider();
    }

    /// <summary>Creates a host service that exposes the local listening port over a Workspaces-owned tunnel.</summary>
    public IDevTunnelHostService CreateHostService()
    {
        var managementClientWrapper = this.CreateManagementClientWrapper();
        var relayHost = new TunnelRelayDevTunnelHost(managementClientWrapper);
        return new DevTunnelHostService(managementClientWrapper, relayHost);
    }

    /// <summary>Creates a client-side endpoint resolver that locates a tunnel by name.</summary>
    public IDevTunnelEndpointResolver CreateEndpointResolver()
    {
        var managementClientWrapper = this.CreateManagementClientWrapper();
        return new DevTunnelEndpointResolver(managementClientWrapper);
    }

    private DevTunnelManagementClientWrapper CreateManagementClientWrapper()
    {
        var managementClient = new TunnelManagementClient(
            UserAgent,
            this.GetAuthenticationHeaderAsync,
            ManagementApiVersions.Version20230927Preview);
        return new DevTunnelManagementClientWrapper(managementClient);
    }

    private async Task<AuthenticationHeaderValue?> GetAuthenticationHeaderAsync()
    {
        var token = await this.authTokenProvider.GetAccessTokenAsync().ConfigureAwait(false);
        return new AuthenticationHeaderValue(TunnelAuthenticationSchemes.GitHub, token);
    }
}
