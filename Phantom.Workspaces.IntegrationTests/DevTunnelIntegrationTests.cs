using System;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.DevTunnel;
using Xunit;

namespace Phantom.Workspaces.IntegrationTests;

/// <summary>
/// End-to-end regression coverage for issue #1082: the tunnel <b>owner</b>, on the same machine and
/// same GitHub account, must be able to resolve and connect a Private-mode dev tunnel by name without
/// a Connect-scope tunnel token. This is the exact "playspace 3 debug" → "playspace 3" flow that
/// previously threw <c>"The Management API did not return a Connect-scope tunnel token."</c> All tests
/// require <c>PHANTOM_INTEGRATION_GITHUB_TOKEN</c> and skip gracefully when it is absent.
/// </summary>
[Collection("DevTunnel")]
public sealed class DevTunnelIntegrationTests : IClassFixture<InProcessDevTunnelPrivateFixture>
{
    private readonly InProcessDevTunnelPrivateFixture fixture;

    public DevTunnelIntegrationTests(InProcessDevTunnelPrivateFixture fixture)
    {
        this.fixture = fixture;
    }

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task DevTunnelPrivateConnect_OwnerSameIdentity_ConnectsWithoutConnectToken()
    {
        var githubToken = Environment.GetEnvironmentVariable("PHANTOM_INTEGRATION_GITHUB_TOKEN")!;
        var factory = new DevTunnelServiceFactory(new StaticTokenProvider(githubToken));
        var resolver = factory.CreateEndpointResolver();

        // Resolve the hosted Private tunnel by name as the same owning GitHub identity. Before the
        // fix this threw because the Management API's list path never returns a Connect token; per
        // design #19 the owner needs no Connect token — the null token is expected and valid.
        var resolution = await resolver.ResolveAsync(
            this.fixture.TunnelName!,
            DevTunnelAccessMode.Private,
            TestContext.Current.CancellationToken);

        Assert.NotNull(resolution);
        Assert.Equal(this.fixture.RelayBaseUri, resolution.BaseUri);
        Assert.Null(resolution.TunnelAuthToken);

        // The identity-derived client path authorizes with the GitHub identity token (and a
        // 401-refresh resolver) rather than sending no X-Tunnel-Authorization header.
        var authorization = DevTunnelClientAuthorization.Resolve(
            resolution,
            DevTunnelAccessMode.Private,
            identityTokenResolver: () => githubToken);

        Assert.Equal(githubToken, authorization.Token);
        Assert.NotNull(authorization.RefreshResolver);

        // Companion assertion: Anonymous mode still returns a null token (no header), so the fix does
        // not regress anonymous tunnels.
        var anonymousResolution = await resolver.ResolveAsync(
            this.fixture.TunnelName!,
            DevTunnelAccessMode.Anonymous,
            TestContext.Current.CancellationToken);

        Assert.Null(anonymousResolution.TunnelAuthToken);
    }

    private sealed class StaticTokenProvider(string token) : IDevTunnelAuthTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(token);
    }
}
