using System;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.DevTunnel;
using Xunit;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Verifies how the dev-tunnel-name client path (issue #1082, Fix A wiring) selects the
/// <c>X-Tunnel-Authorization</c> token: Private connect is identity-derived (design #19), so a null
/// Connect token must fall back to the GitHub identity token and a 401-refresh resolver rather than
/// sending no header.
/// </summary>
public sealed class DevTunnelClientAuthorizationTests
{
    [Fact]
    public void CreateDevTunnelNameDataAccessLayer_PrivateMode_WithNullToken_UsesGitHubIdentityHeader()
    {
        var resolution = new DevTunnelEndpointResolution(new Uri("https://tunnel-abc-5280.usw2.devtunnels.ms/"), TunnelAuthToken: null);

        var authorization = DevTunnelClientAuthorization.Resolve(
            resolution,
            DevTunnelAccessMode.Private,
            identityTokenResolver: () => "github-identity-token");

        Assert.Equal("github-identity-token", authorization.Token);
        Assert.NotNull(authorization.RefreshResolver);
        Assert.Equal("github-identity-token", authorization.RefreshResolver!());
    }

    [Fact]
    public void Resolve_PrivateMode_WithExplicitConnectToken_UsesTokenVerbatim_NoIdentityFallback()
    {
        var resolution = new DevTunnelEndpointResolution(new Uri("https://tunnel-abc-5280.usw2.devtunnels.ms/"), TunnelAuthToken: "api-connect-token");
        var identityResolverInvoked = false;

        var authorization = DevTunnelClientAuthorization.Resolve(
            resolution,
            DevTunnelAccessMode.Private,
            identityTokenResolver: () => { identityResolverInvoked = true; return "github-identity-token"; });

        Assert.Equal("api-connect-token", authorization.Token);
        Assert.Null(authorization.RefreshResolver);
        Assert.False(identityResolverInvoked);
    }

    [Fact]
    public void Resolve_AnonymousMode_WithNullToken_SendsNoAuthorization()
    {
        var resolution = new DevTunnelEndpointResolution(new Uri("https://tunnel-abc-5280.usw2.devtunnels.ms/"), TunnelAuthToken: null);
        var identityResolverInvoked = false;

        var authorization = DevTunnelClientAuthorization.Resolve(
            resolution,
            DevTunnelAccessMode.Anonymous,
            identityTokenResolver: () => { identityResolverInvoked = true; return "github-identity-token"; });

        Assert.Null(authorization.Token);
        Assert.Null(authorization.RefreshResolver);
        Assert.False(identityResolverInvoked);
    }
}
