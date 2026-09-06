using System;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DevTunnels.Management;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Llm.Auth;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Composition helper that builds the Dev Tunnels SDK <see cref="ITunnelManagementClient"/> and the
/// service objects that compose it (host service, endpoint resolver, Connect-token provider), wired to a
/// per-scheme <see cref="IDevTunnelAuthTokenProvider"/> for management identity (issue #1458). The SDK
/// access-token callback supplies the authentication header for the <em>selected</em> scheme
/// (<c>github</c>, <c>aad</c>, or none for anonymous), not a hard-coded GitHub header. The concrete SDK
/// wrapper types stay internal behind the returned interfaces.
/// </summary>
public sealed class DevTunnelServiceFactory
{
    private static readonly ProductInfoHeaderValue UserAgent = new("Phantom.Workspaces", "1.0");

    private readonly IDevTunnelAuthTokenProvider authTokenProvider;
    private readonly RemoteAuthentication? authentication;
    private readonly DevTunnelAuthProviderDependencies dependencies;

    /// <summary>
    /// Legacy composition entry point: uses <paramref name="authTokenProvider"/> (defaulting to the GitHub
    /// scheme) for the Management-API identity. Preserved so existing GitHub-only call sites are unchanged.
    /// </summary>
    public DevTunnelServiceFactory(IDevTunnelAuthTokenProvider? authTokenProvider = null)
    {
        this.authTokenProvider = authTokenProvider ?? new GitHubDevTunnelAuthTokenProvider();
        this.authentication = null;
        this.dependencies = DevTunnelAuthProviderDependencies.Default;
    }

    /// <summary>
    /// Scheme-driven composition entry point (issue #1458): selects the identity provider from
    /// <paramref name="authentication"/>'s <see cref="RemoteAuthentication.Scheme"/> using the host seams
    /// in <paramref name="dependencies"/>.
    /// </summary>
    public DevTunnelServiceFactory(RemoteAuthentication? authentication, DevTunnelAuthProviderDependencies? dependencies = null)
    {
        this.dependencies = dependencies ?? DevTunnelAuthProviderDependencies.Default;
        this.authentication = authentication;
        this.authTokenProvider = DevTunnelAuthTokenProviderFactory.Create(authentication, this.dependencies);
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

    /// <summary>
    /// Creates the Wave-3 (#1458) Connect-token provider that fills the #1456 handler seam: it uses the
    /// selected identity to mint a Connect-scope token at the Management API (via the endpoint resolver),
    /// caches it in memory, and — when <see cref="RemoteAuthentication.Remember"/> — persists/reuses it
    /// under <c>devtunnel:&lt;name&gt;</c> so a remembered+cached identity connects silently. A relay 401
    /// (surfaced by the handler as a refresh) re-mints; a Management-API unauthorized refreshes the
    /// identity first, bounded to one interactive prompt per episode.
    /// </summary>
    public IDevTunnelConnectTokenProvider CreateConnectTokenProvider(string tunnelName, DevTunnelAccessMode accessMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tunnelName);

        var resolver = this.CreateEndpointResolver();
        var remember = this.authentication?.Remember ?? false;
        var cacheKeyName = string.IsNullOrWhiteSpace(this.authentication?.TokenCacheKey)
            ? tunnelName
            : this.authentication!.TokenCacheKey!;

        var persistentCache = CredentialManagerTokenCache.TokenCacheFor(
            this.dependencies.PlatformSecretStore,
            ManagementApiDevTunnelConnectTokenProvider.CacheKeyPrefix,
            cacheKeyName,
            this.dependencies.LoggerFactory);

        return new ManagementApiDevTunnelConnectTokenProvider(
            this.authTokenProvider,
            (_, cancellationToken) => MintConnectTokenAsync(resolver, tunnelName, accessMode, cancellationToken),
            cacheKeyName,
            remember,
            persistentCache,
            this.dependencies.ConsentStore,
            this.dependencies.TimeProvider);
    }

    /// <summary>
    /// Mints a Connect-scope token by resolving the tunnel's relay endpoint through the Management API. The
    /// identity token is supplied to the Management client via <see cref="GetAuthenticationHeaderAsync"/>;
    /// an ownership/authorization failure is surfaced as
    /// <see cref="DevTunnelManagementUnauthorizedException"/> so the provider refreshes the identity and
    /// re-mints.
    /// </summary>
    private static async Task<string?> MintConnectTokenAsync(
        IDevTunnelEndpointResolver resolver,
        string tunnelName,
        DevTunnelAccessMode accessMode,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await resolver.ResolveAsync(tunnelName, accessMode, cancellationToken).ConfigureAwait(false);
            return resolution.TunnelAuthToken;
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new DevTunnelManagementUnauthorizedException(exception.Message, exception);
        }
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
        // Anonymous scheme (null) sends no Management-API auth header at all.
        var scheme = this.authTokenProvider.AuthenticationScheme;
        if (string.IsNullOrEmpty(scheme))
        {
            return null;
        }

        var token = await this.authTokenProvider.GetAccessTokenAsync().ConfigureAwait(false);
        return string.IsNullOrEmpty(token) ? null : new AuthenticationHeaderValue(scheme, token);
    }
}
