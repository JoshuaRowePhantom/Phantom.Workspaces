using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.DevTunnel;
using Xunit;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Issue #1458: verifies the pluggable per-scheme dev-tunnel auth providers (github / entra / oauth /
/// anonymous), the Management-API Connect-token minting/caching provider (persist + silent remembered
/// reuse under the <c>devtunnel:&lt;name&gt;</c> key), and the failure → refresh → re-auth policy
/// (Management-API unauthorized refreshes the identity then re-mints, bounded to one interactive prompt).
/// </summary>
public sealed class DevTunnelAuthProviderTests
{
    private const string CachePrefix = ManagementApiDevTunnelConnectTokenProvider.CacheKeyPrefix;

    [Fact]
    public async Task EntraDevTunnelAuthProvider_AcquiresIdentity_ViaSharedEntraFactory()
    {
        EntraPinnedTokenRequest? captured = null;
        var credential = new RecordingCredential("entra-identity-token");
        var authentication = new RemoteAuthentication(
            Scheme: RemoteAuthentication.EntraScheme,
            Authority: "https://login.microsoftonline.com/contoso/v2.0",
            ClientId: "client-1",
            Scopes: new[] { "api://tunnels/.default" });

        var provider = new EntraDevTunnelAuthProvider(
            authentication,
            credentialFactory: request =>
            {
                captured = request;
                return credential;
            });

        var token = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        // The identity was acquired through the shared #1454 Entra surface (credential factory +
        // EntraPinnedTokenProvider), carrying the authority/client id/scopes from RemoteAuthentication.
        Assert.Equal("aad", provider.AuthenticationScheme);
        Assert.Equal("entra-identity-token", token);
        Assert.NotNull(captured);
        Assert.Equal("https://login.microsoftonline.com/contoso/v2.0", captured!.Authority);
        Assert.Equal("client-1", captured.ClientId);
        Assert.Equal(new[] { "api://tunnels/.default" }, credential.LastScopes);
    }

    [Fact]
    public async Task DevTunnelConnectToken_PersistsAndReloads_UnderDevTunnelCacheKey()
    {
        var store = new FakePlatformSecretStore();
        var identity = new FakeIdentityProvider("id-token");
        var mintCount = 0;
        Task<string?> Mint(string _, CancellationToken __)
        {
            mintCount++;
            return Task.FromResult<string?>("connect-token-1");
        }

        var minter = new ManagementApiDevTunnelConnectTokenProvider(
            identity,
            Mint,
            tunnelName: "my-tunnel",
            remember: true,
            persistentCache: new CredentialManagerTokenCache(store, CachePrefix, "my-tunnel"));

        var first = await minter.GetConnectTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("connect-token-1", first);
        Assert.Equal(1, mintCount);
        Assert.Equal(CachePrefix + "my-tunnel", Assert.Single(store.Secrets.Keys));

        // A brand-new provider over the same persistent store reloads the Connect token without re-minting
        // and without ever touching the identity (proved by a throwing identity provider).
        var reloaded = new ManagementApiDevTunnelConnectTokenProvider(
            new ThrowingIdentityProvider(),
            Mint,
            tunnelName: "my-tunnel",
            remember: true,
            persistentCache: new CredentialManagerTokenCache(store, CachePrefix, "my-tunnel"));

        var second = await reloaded.GetConnectTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("connect-token-1", second);
        Assert.Equal(1, mintCount); // no additional mint on reload
    }

    [Fact]
    public async Task DevTunnelAuth_RememberedConsent_ConnectsSilentlyWithoutPrompt()
    {
        var store = new FakePlatformSecretStore();

        // First connect (remember=true) mints and persists the Connect token.
        var seedIdentity = new FakeIdentityProvider("id-token");
        var seed = new ManagementApiDevTunnelConnectTokenProvider(
            seedIdentity,
            (_, _) => Task.FromResult<string?>("connect-token"),
            tunnelName: "remembered",
            remember: true,
            persistentCache: new CredentialManagerTokenCache(store, CachePrefix, "remembered"));
        await seed.GetConnectTokenAsync(TestContext.Current.CancellationToken);

        // A later connect with a remembered+cached identity must reuse silently: the identity provider is
        // never asked for a token (no interactive prompt) and no new mint occurs.
        var laterIdentity = new FakeIdentityProvider("id-token");
        var mintCalls = 0;
        var later = new ManagementApiDevTunnelConnectTokenProvider(
            laterIdentity,
            (_, _) => { mintCalls++; return Task.FromResult<string?>("should-not-mint"); },
            tunnelName: "remembered",
            remember: true,
            persistentCache: new CredentialManagerTokenCache(store, CachePrefix, "remembered"));

        var token = await later.GetConnectTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("connect-token", token);
        Assert.Equal(0, mintCalls);
        Assert.Equal(0, laterIdentity.GetCount);      // no interactive identity acquisition
        Assert.Equal(0, laterIdentity.RefreshCount);
    }

    [Fact]
    public async Task DevTunnelAuth_ManagementApi401_RefreshesIdentityThenReMints()
    {
        var identity = new FakeIdentityProvider(initial: "id-stale", refreshed: "id-fresh");
        Task<string?> Mint(string identityToken, CancellationToken _)
            => identityToken == "id-stale"
                ? throw new DevTunnelManagementUnauthorizedException()
                : Task.FromResult<string?>("connect-fresh");

        var minter = new ManagementApiDevTunnelConnectTokenProvider(
            identity,
            Mint,
            tunnelName: "t",
            remember: false);

        var token = await minter.GetConnectTokenAsync(TestContext.Current.CancellationToken);

        // The Management-API unauthorized triggered exactly one identity refresh, then a successful re-mint.
        Assert.Equal("connect-fresh", token);
        Assert.Equal(1, identity.RefreshCount);
    }

    [Fact]
    public async Task DevTunnelAuth_ReAuthEpisode_BoundedToOneInteractivePrompt()
    {
        var identity = new FakeIdentityProvider(initial: "id-1", refreshed: "id-2");
        var mintAttempts = 0;
        Task<string?> Mint(string _, CancellationToken __)
        {
            mintAttempts++;
            throw new DevTunnelManagementUnauthorizedException();
        }

        var minter = new ManagementApiDevTunnelConnectTokenProvider(
            identity,
            Mint,
            tunnelName: "t",
            remember: false);

        await Assert.ThrowsAsync<DevTunnelManagementUnauthorizedException>(
            async () => await minter.RefreshConnectTokenAsync(TestContext.Current.CancellationToken));

        // Even under a persistent auth failure the episode prompts the user (refreshes the identity) at
        // most once and attempts the re-mint at most twice (original + one post-refresh).
        Assert.Equal(1, identity.RefreshCount);
        Assert.Equal(2, mintAttempts);
    }

    [Fact]
    public async Task DevTunnelAuth_GithubAndAnonymous_BehaviorPreserved()
    {
        // github: the factory selects the GitHub provider, which resolves GITHUB_TOKEN and carries the
        // github Management-API scheme.
        var github = DevTunnelAuthTokenProviderFactory.Create(
            new RemoteAuthentication(RemoteAuthentication.GithubScheme));
        Assert.IsType<GitHubDevTunnelAuthTokenProvider>(github);
        Assert.Equal("github", github.AuthenticationScheme);

        var previous = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", "ghs_preserved");
            Assert.Equal("ghs_preserved", await github.GetAccessTokenAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", previous);
        }

        // anonymous: the factory selects the anonymous provider, which attaches no identity (null scheme,
        // empty token) and yields a null Connect token (no X-Tunnel-Authorization header).
        var anonymous = DevTunnelAuthTokenProviderFactory.Create(
            new RemoteAuthentication(RemoteAuthentication.AnonymousScheme));
        Assert.IsType<AnonymousDevTunnelAuthProvider>(anonymous);
        Assert.Null(anonymous.AuthenticationScheme);
        Assert.Equal(string.Empty, await anonymous.GetAccessTokenAsync(TestContext.Current.CancellationToken));

        var anonymousConnect = new ManagementApiDevTunnelConnectTokenProvider(
            anonymous,
            (_, _) => Task.FromResult<string?>(null),
            tunnelName: "anon",
            remember: false);
        Assert.Null(await anonymousConnect.GetConnectTokenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OAuthDevTunnelAuthProvider_AcquiresIdentity_ViaSharedRedirectHandler()
    {
        OAuthTokenRequest? captured = null;
        var authentication = new RemoteAuthentication(
            Scheme: RemoteAuthentication.OAuthScheme,
            ClientId: "oauth-client",
            AuthorizationEndpoint: "https://issuer.example/authorize",
            TokenEndpoint: "https://issuer.example/token",
            Scopes: new[] { "tunnel.read" });

        var provider = new OAuthDevTunnelAuthProvider(
            authentication,
            acquireToken: (request, _) =>
            {
                captured = request;
                return Task.FromResult("oauth-identity-token");
            });

        var token = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        // The generic OAuth identity is acquired through the shared interactive redirect-handler seam and
        // carried under the Bearer Management-API scheme, forwarding the RemoteAuthentication parameters.
        Assert.Equal("Bearer", provider.AuthenticationScheme);
        Assert.Equal("oauth-identity-token", token);
        Assert.NotNull(captured);
        Assert.Equal("oauth-client", captured!.ClientId);
        Assert.Equal("https://issuer.example/authorize", captured.AuthorizationEndpoint);
        Assert.Equal("https://issuer.example/token", captured.TokenEndpoint);
        Assert.Equal(new[] { "tunnel.read" }, captured.Scopes);
    }

    [Fact]
    public async Task OAuthDevTunnelAuthProvider_SingleFlightCaching_ReusesTokenUntilRefreshInvalidates()
    {
        var acquisitions = 0;
        var authentication = new RemoteAuthentication(RemoteAuthentication.OAuthScheme, ClientId: "c");
        var provider = new OAuthDevTunnelAuthProvider(
            authentication,
            acquireToken: (_, _) =>
            {
                var count = Interlocked.Increment(ref acquisitions);
                return Task.FromResult($"oauth-token-{count}");
            });

        var first = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        var second = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        // Single-flight caching: the second read reuses the cached token without a second acquisition.
        Assert.Equal("oauth-token-1", first);
        Assert.Equal("oauth-token-1", second);
        Assert.Equal(1, acquisitions);

        var refreshed = await provider.RefreshAccessTokenAsync(TestContext.Current.CancellationToken);
        var afterRefresh = await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        // Refresh invalidates the cache, forcing exactly one re-acquisition that the next read then reuses.
        Assert.Equal("oauth-token-2", refreshed);
        Assert.Equal("oauth-token-2", afterRefresh);
        Assert.Equal(2, acquisitions);
    }

    [Fact]
    public async Task OAuthDevTunnelAuthProvider_DefaultResolver_ResolvesEnvPlaceholderAndLiteralClientSecret()
    {
        const string envName = "PW_OAUTH_DEVTUNNEL_TEST_SECRET";
        var previous = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable(envName, "env-secret-value");

            // ${ENV:NAME} placeholder → resolved from the environment by the default resolver.
            Assert.Equal("env-secret-value", await CaptureResolvedClientSecretAsync("${" + "ENV:" + envName + "}"));

            // A literal (non-placeholder) value passes through unchanged.
            Assert.Equal("literal-secret", await CaptureResolvedClientSecretAsync("literal-secret"));

            // A blank client secret resolves to null (no secret materialized on the request).
            Assert.Null(await CaptureResolvedClientSecretAsync(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envName, previous);
        }
    }

    [Fact]
    public async Task OAuthDevTunnelAuthProvider_InjectedSecretResolver_MaterializesSecretStorePlaceholder()
    {
        // The oauth provider defers ${SECRET:name} materialization to the injected resolver (production
        // wires a secrets-store-backed resolver); the resolved value flows into the acquisition request and
        // a raw secret is never held on the configuration.
        const string placeholder = "${SECRET:devtunnel-client-secret}";
        var store = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["devtunnel-client-secret"] = "resolved-store-secret",
        };
        string? capturedPlaceholder = null;
        OAuthTokenRequest? capturedRequest = null;

        var authentication = new RemoteAuthentication(
            RemoteAuthentication.OAuthScheme,
            ClientId: "c",
            ClientSecret: placeholder);

        var provider = new OAuthDevTunnelAuthProvider(
            authentication,
            acquireToken: (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult("token");
            },
            secretResolver: (value, _) =>
            {
                capturedPlaceholder = value;
                var name = value is not null
                    && value.StartsWith("${SECRET:", StringComparison.Ordinal)
                    && value.EndsWith("}", StringComparison.Ordinal)
                        ? value["${SECRET:".Length..^1]
                        : null;
                return new ValueTask<string?>(name is not null && store.TryGetValue(name, out var secret) ? secret : null);
            });

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(placeholder, capturedPlaceholder);
        Assert.NotNull(capturedRequest);
        Assert.Equal("resolved-store-secret", capturedRequest!.ClientSecret);
    }

    private static async Task<string?> CaptureResolvedClientSecretAsync(string? clientSecret)
    {
        OAuthTokenRequest? captured = null;
        var authentication = new RemoteAuthentication(
            RemoteAuthentication.OAuthScheme,
            ClientId: "c",
            ClientSecret: clientSecret);
        var provider = new OAuthDevTunnelAuthProvider(
            authentication,
            acquireToken: (request, _) =>
            {
                captured = request;
                return Task.FromResult("token");
            });

        await provider.GetAccessTokenAsync(TestContext.Current.CancellationToken);
        return captured!.ClientSecret;
    }

    private sealed class FakeIdentityProvider(string initial, string? refreshed = null) : IDevTunnelAuthTokenProvider
    {
        private readonly string initial = initial;
        private readonly string refreshed = refreshed ?? initial;
        private int refreshCount;
        private int getCount;

        public int RefreshCount => Volatile.Read(ref this.refreshCount);

        public int GetCount => Volatile.Read(ref this.getCount);

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.getCount);
            return Task.FromResult(this.RefreshCount > 0 ? this.refreshed : this.initial);
        }

        public Task<string> RefreshAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.refreshCount);
            return Task.FromResult(this.refreshed);
        }
    }

    private sealed class ThrowingIdentityProvider : IDevTunnelAuthTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The identity must not be acquired for a silent cached reuse.");
    }

    private sealed class RecordingCredential(string token) : TokenCredential
    {
        private readonly string token = token;

        public string[]? LastScopes { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            this.LastScopes = requestContext.Scopes;
            return new AccessToken(this.token, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(this.GetToken(requestContext, cancellationToken));
    }

    private sealed class FakePlatformSecretStore : IPlatformSecretStore
    {
        public Dictionary<string, SecureString> Secrets { get; } = new(StringComparer.Ordinal);

        public Task<SecureString?> ReadAsync(string name, CancellationToken ct)
            => Task.FromResult(this.Secrets.TryGetValue(name, out var value) ? Copy(value) : null);

        public Task WriteAsync(string name, SecureString value, CancellationToken ct)
        {
            this.Secrets[name] = Copy(value);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken ct)
        {
            this.Secrets.Remove(name);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken ct)
        {
            IReadOnlyList<string> names = this.Secrets.Keys
                .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();
            return Task.FromResult(names);
        }

        private static SecureString Copy(SecureString value)
            => Phantom.Workspaces.Llm.Secrets.SecureStringMarshal.Use(value, ToSecureString);

        private static SecureString ToSecureString(string value)
        {
            var secure = new SecureString();
            foreach (var character in value)
            {
                secure.AppendChar(character);
            }

            secure.MakeReadOnly();
            return secure;
        }
    }
}
