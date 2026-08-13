using System;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.DevTunnel;
using Xunit;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Issue #1293: The dev-tunnels relay (<c>*.devtunnels.ms</c>) only accepts a Microsoft-issued
/// Connect-scope tunnel access token; a GitHub OAuth identity token is always rejected with
/// 401 (empty body). These tests pin the fixed behaviour of <see cref="DevTunnelClientAuthorization.Resolve"/>:
/// Connect token verbatim, no header for Anonymous, actionable throw for Private + no Connect
/// token (no silent GitHub-token fallback).
/// </summary>
public sealed class DevTunnelClientAuthorizationTests
{
    private static DevTunnelEndpointResolution CreateResolution(string? tunnelAuthToken)
        => new(new Uri("https://tunnel-abc-5280.usw2.devtunnels.ms/"), tunnelAuthToken);

    [Fact]
    public void Resolve_PrivateMode_WithExplicitConnectToken_UsesTokenVerbatim()
    {
        var resolution = CreateResolution("api-connect-token");

        var authorization = DevTunnelClientAuthorization.Resolve(resolution, DevTunnelAccessMode.Private);

        Assert.Equal("api-connect-token", authorization.Token);
        Assert.Null(authorization.RefreshResolver);
    }

    [Fact]
    public void Resolve_AnonymousMode_WithNoConnectToken_SendsNoAuthorization()
    {
        var resolution = CreateResolution(tunnelAuthToken: null);

        var authorization = DevTunnelClientAuthorization.Resolve(resolution, DevTunnelAccessMode.Anonymous);

        Assert.Null(authorization.Token);
        Assert.Null(authorization.RefreshResolver);
    }

    [Fact]
    public void Resolve_AnonymousMode_WithConnectToken_UsesTokenVerbatim()
    {
        // Documents that Connect-token-present precedes access-mode dispatch, so even Anonymous
        // dispatch uses an explicit token if the Management API happened to mint one.
        var resolution = CreateResolution("api-connect-token");

        var authorization = DevTunnelClientAuthorization.Resolve(resolution, DevTunnelAccessMode.Anonymous);

        Assert.Equal("api-connect-token", authorization.Token);
    }

    [Fact]
    public void Resolve_PrivateMode_WithNoConnectToken_ThrowsActionableError()
    {
        var resolution = CreateResolution(tunnelAuthToken: null);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DevTunnelClientAuthorization.Resolve(resolution, DevTunnelAccessMode.Private));

        // Actionable message: names the relay host and calls out the missing Connect-scope token
        // plus the ownership/label root cause the maintainer must check.
        Assert.Contains("Connect-scope", ex.Message, StringComparison.Ordinal);
        Assert.Contains("tunnel-abc-5280.usw2.devtunnels.ms", ex.Message, StringComparison.Ordinal);
        Assert.Contains("label", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_PrivateMode_WithNoConnectToken_DoesNotSendGitHubIdentityToken()
    {
        // Structural guarantee for the #1293 fix: the Resolve API has no identity-token input at
        // all, so no caller can accidentally reintroduce the buggy X-Tunnel-Authorization fallback.
        // Combined with the throw, this proves the GitHub token can never appear on the wire.
        var resolution = CreateResolution(tunnelAuthToken: null);

        Assert.Throws<InvalidOperationException>(() =>
            DevTunnelClientAuthorization.Resolve(resolution, DevTunnelAccessMode.Private));

        var resolveMethod = typeof(DevTunnelClientAuthorization).GetMethod(nameof(DevTunnelClientAuthorization.Resolve));
        Assert.NotNull(resolveMethod);
        Assert.DoesNotContain(
            resolveMethod!.GetParameters(),
            p => p.Name is not null && p.Name.Contains("identity", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            resolveMethod.GetParameters(),
            p => p.Name is not null && p.Name.Contains("github", StringComparison.OrdinalIgnoreCase));
    }
}
