using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.DevTunnel;
using Xunit;

namespace Phantom.Workspaces.IntegrationTests;

/// <summary>
/// Integration tests verifying end-to-end DevTunnel access control for Private mode:
/// connect-token issuance, enforcement at the relay, and automatic propagation through the
/// endpoint resolver. All tests require <c>PHANTOM_INTEGRATION_GITHUB_TOKEN</c> and skip
/// gracefully when it is absent.
/// </summary>
[Collection("DevTunnel")]
public sealed class DevTunnelAccessControlTests : IClassFixture<InProcessDevTunnelPrivateFixture>
{
    private readonly InProcessDevTunnelPrivateFixture fixture;

    public DevTunnelAccessControlTests(InProcessDevTunnelPrivateFixture fixture)
    {
        this.fixture = fixture;
    }

    // ── Host status ────────────────────────────────────────────────────────

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public void PrivateMode_HostStatus_ExposesNonEmptyConnectToken()
    {
        Assert.NotNull(this.fixture.ConnectToken);
        Assert.NotEmpty(this.fixture.ConnectToken!);
    }

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public void PrivateMode_ConnectToken_DiffersFromGitHubOAuthToken()
    {
        var githubToken = Environment.GetEnvironmentVariable("PHANTOM_INTEGRATION_GITHUB_TOKEN");
        Assert.NotNull(githubToken);
        Assert.NotEqual(githubToken, this.fixture.ConnectToken);
    }

    // ── Relay access enforcement ───────────────────────────────────────────

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task PrivateMode_ConnectWithApiIssuedConnectToken_Succeeds()
    {
        using var client = BuildHttpClient(tunnelToken: this.fixture.ConnectToken);
        var response = await client.GetAsync(this.fixture.RelayBaseUri, TestContext.Current.CancellationToken);

        // The Kestrel server returns a non-401/403 status for authenticated requests.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task PrivateMode_ConnectWithNoToken_Returns401OrForbidden()
    {
        using var client = BuildHttpClient(tunnelToken: null);
        var response = await client.GetAsync(this.fixture.RelayBaseUri, TestContext.Current.CancellationToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401 or 403 when no token is supplied; got {(int)response.StatusCode}.");
    }

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task PrivateMode_ConnectWithRawGitHubOAuthToken_Returns401OrForbidden()
    {
        // The relay must reject a raw GitHub OAuth token — it is not a tunnel-scoped connect token.
        var githubToken = Environment.GetEnvironmentVariable("PHANTOM_INTEGRATION_GITHUB_TOKEN");
        using var client = BuildHttpClient(tunnelToken: githubToken);
        var response = await client.GetAsync(this.fixture.RelayBaseUri, TestContext.Current.CancellationToken);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401 or 403 when a GitHub OAuth token is supplied as tunnel auth; got {(int)response.StatusCode}. " +
            "A raw GitHub token must not be accepted by the relay as a connect token.");
    }

    // ── Endpoint resolver ──────────────────────────────────────────────────

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task EndpointResolver_PrivateMode_ConnectTokenIsNotNull()
    {
        var githubToken = Environment.GetEnvironmentVariable("PHANTOM_INTEGRATION_GITHUB_TOKEN")!;
        var factory = new DevTunnelServiceFactory(new StaticTokenProvider(githubToken));
        var resolver = factory.CreateEndpointResolver();

        var resolution = await resolver.ResolveAsync(
            this.fixture.TunnelName!,
            DevTunnelAccessMode.Private,
            TestContext.Current.CancellationToken);

        Assert.NotNull(resolution.TunnelAuthToken);
        Assert.NotEmpty(resolution.TunnelAuthToken!);
    }

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task EndpointResolver_AnonymousMode_ConnectTokenIsNull()
    {
        // For the anonymous-mode tunnel used by the base DevTunnel fixture, the resolver
        // returns null — no X-Tunnel-Authorization header needed.
        // We verify this by resolving our private-mode tunnel with Anonymous overriding the mode:
        // the resolver yields null regardless of whether the tunnel itself is private.
        var githubToken = Environment.GetEnvironmentVariable("PHANTOM_INTEGRATION_GITHUB_TOKEN")!;
        var factory = new DevTunnelServiceFactory(new StaticTokenProvider(githubToken));
        var resolver = factory.CreateEndpointResolver();

        var resolution = await resolver.ResolveAsync(
            this.fixture.TunnelName!,
            DevTunnelAccessMode.Anonymous,
            TestContext.Current.CancellationToken);

        Assert.Null(resolution.TunnelAuthToken);
    }

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task EndpointResolver_PrivateMode_DoesNotRequireEnvVarConfiguration()
    {
        // The connect token must be fetched automatically from the Management API;
        // no GITHUB_TUNNEL_TOKEN or similar env var should be required.
        // We verify this by checking that resolution succeeds using only the GitHub identity token.
        var githubToken = Environment.GetEnvironmentVariable("PHANTOM_INTEGRATION_GITHUB_TOKEN")!;
        var factory = new DevTunnelServiceFactory(new StaticTokenProvider(githubToken));
        var resolver = factory.CreateEndpointResolver();

        // Should not throw — connect token comes from the Management API automatically.
        var resolution = await resolver.ResolveAsync(
            this.fixture.TunnelName!,
            DevTunnelAccessMode.Private,
            TestContext.Current.CancellationToken);

        Assert.NotNull(resolution.TunnelAuthToken);
    }

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task EndpointResolver_PrivateMode_ConnectToken_DiffersFromGitHubAuthToken()
    {
        var githubToken = Environment.GetEnvironmentVariable("PHANTOM_INTEGRATION_GITHUB_TOKEN")!;
        var factory = new DevTunnelServiceFactory(new StaticTokenProvider(githubToken));
        var resolver = factory.CreateEndpointResolver();

        var resolution = await resolver.ResolveAsync(
            this.fixture.TunnelName!,
            DevTunnelAccessMode.Private,
            TestContext.Current.CancellationToken);

        // The connect token is a tunnel-scoped API-issued token, not the raw GitHub OAuth token.
        Assert.NotEqual(githubToken, resolution.TunnelAuthToken);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static HttpClient BuildHttpClient(string? tunnelToken)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrWhiteSpace(tunnelToken))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "X-Tunnel-Authorization", $"tunnel {tunnelToken}");
        }

        return client;
    }

    private sealed class StaticTokenProvider(string token) : IDevTunnelAuthTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(token);
    }
}
