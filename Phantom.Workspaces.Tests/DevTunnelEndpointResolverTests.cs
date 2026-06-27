using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.DevTunnel;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class DevTunnelEndpointResolverTests
{
    [Fact]
    public async Task ResolveAsync_BuildsRelayUriFromSingleForwardedPort_WithoutUserSuppliedPort()
    {
        var lookupClient = new FakeLookupClient(new DevTunnelLookupResult("tunnel-abc", "usw2", [5280]));
        var resolver = new DevTunnelEndpointResolver(lookupClient);

        var resolution = await resolver.ResolveAsync("my-tunnel", DevTunnelAccessMode.Private, TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://tunnel-abc-5280.usw2.devtunnels.ms/"), resolution.BaseUri);
        Assert.Null(resolution.TunnelAuthToken);
        Assert.Equal("my-tunnel", lookupClient.LookedUpName);
    }

    [Fact]
    public async Task ResolveAsync_PrivateMode_ReturnsNullToken()
    {
        var resolver = new DevTunnelEndpointResolver(
            new FakeLookupClient(new DevTunnelLookupResult("tunnel-abc", "usw2", [5280])),
            accessTokenSource: "PW_TUNNEL_TOKEN",
            tokenSourceResolver: _ => "should-not-be-used");

        var resolution = await resolver.ResolveAsync("my-tunnel", DevTunnelAccessMode.Private, TestContext.Current.CancellationToken);

        Assert.Null(resolution.TunnelAuthToken);
    }

    [Fact]
    public async Task ResolveAsync_TokenMode_ResolvesPreSharedTokenFromSource()
    {
        var resolver = new DevTunnelEndpointResolver(
            new FakeLookupClient(new DevTunnelLookupResult("tunnel-abc", "usw2", [5280])),
            accessTokenSource: "PW_TUNNEL_TOKEN",
            tokenSourceResolver: sourceName => sourceName == "PW_TUNNEL_TOKEN" ? "secret-token" : null);

        var resolution = await resolver.ResolveAsync("my-tunnel", DevTunnelAccessMode.Token, TestContext.Current.CancellationToken);

        Assert.Equal("secret-token", resolution.TunnelAuthToken);
    }

    [Fact]
    public async Task ResolveAsync_TokenMode_WithoutSource_Throws()
    {
        var resolver = new DevTunnelEndpointResolver(
            new FakeLookupClient(new DevTunnelLookupResult("tunnel-abc", "usw2", [5280])));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("my-tunnel", DevTunnelAccessMode.Token, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_WhenNotExactlyOnePort_Throws()
    {
        var resolverNoPorts = new DevTunnelEndpointResolver(
            new FakeLookupClient(new DevTunnelLookupResult("tunnel-abc", "usw2", [])));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolverNoPorts.ResolveAsync("my-tunnel", DevTunnelAccessMode.Private, TestContext.Current.CancellationToken));

        var resolverTwoPorts = new DevTunnelEndpointResolver(
            new FakeLookupClient(new DevTunnelLookupResult("tunnel-abc", "usw2", [5280, 6000])));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolverTwoPorts.ResolveAsync("my-tunnel", DevTunnelAccessMode.Private, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_WithAutoSelector_DiscoversSingleTunnel_WithoutLookupByName()
    {
        var lookupClient = new FakeLookupClient(new DevTunnelLookupResult("tunnel-auto", "usw2", [5280]));
        var resolver = new DevTunnelEndpointResolver(lookupClient);

        var resolution = await resolver.ResolveAsync("auto", DevTunnelAccessMode.Private, TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://tunnel-auto-5280.usw2.devtunnels.ms/"), resolution.BaseUri);
        Assert.True(lookupClient.DiscoverCalled);
        Assert.Null(lookupClient.LookedUpName);
    }

    [Fact]
    public async Task ResolveAsync_WithBlankName_DiscoversSingleTunnel()
    {
        var lookupClient = new FakeLookupClient(new DevTunnelLookupResult("tunnel-auto", "usw2", [5280]));
        var resolver = new DevTunnelEndpointResolver(lookupClient);

        var resolution = await resolver.ResolveAsync("   ", DevTunnelAccessMode.Private, TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://tunnel-auto-5280.usw2.devtunnels.ms/"), resolution.BaseUri);
        Assert.True(lookupClient.DiscoverCalled);
    }

    [Fact]
    public async Task ResolveAsync_WithAutoSelector_PropagatesDiscoveryFailure()
    {
        var lookupClient = new FakeLookupClient(discoverException: new InvalidOperationException("ambiguous"));
        var resolver = new DevTunnelEndpointResolver(lookupClient);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("auto", DevTunnelAccessMode.Private, TestContext.Current.CancellationToken));
    }

    private sealed class FakeLookupClient : IDevTunnelLookupClient
    {
        private readonly DevTunnelLookupResult? result;
        private readonly Exception? discoverException;

        public FakeLookupClient(DevTunnelLookupResult? result = null, Exception? discoverException = null)
        {
            this.result = result;
            this.discoverException = discoverException;
        }

        public string? LookedUpName { get; private set; }

        public bool DiscoverCalled { get; private set; }

        public Task<DevTunnelLookupResult> LookupByNameAsync(string tunnelName, CancellationToken cancellationToken = default)
        {
            this.LookedUpName = tunnelName;
            return Task.FromResult(this.result ?? throw new InvalidOperationException("no result configured"));
        }

        public Task<DevTunnelLookupResult> DiscoverSingleAsync(CancellationToken cancellationToken = default)
        {
            this.DiscoverCalled = true;
            if (this.discoverException is not null)
            {
                throw this.discoverException;
            }

            return Task.FromResult(this.result ?? throw new InvalidOperationException("no result configured"));
        }
    }
}
