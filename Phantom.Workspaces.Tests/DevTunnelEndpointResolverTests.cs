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
        var lookupClient = new FakeLookupClient(new DevTunnelLookupResult("tunnel-abc", "usw2", [5280], ConnectToken: "connect-token"));
        var resolver = new DevTunnelEndpointResolver(lookupClient);

        var resolution = await resolver.ResolveAsync("my-tunnel", DevTunnelAccessMode.Private, TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://tunnel-abc-5280.usw2.devtunnels.ms/"), resolution.BaseUri);
        Assert.Equal("connect-token", resolution.TunnelAuthToken);
        Assert.Equal("my-tunnel", lookupClient.LookedUpName);
    }

    [Fact]
    public async Task ResolveAsync_PrivateMode_UsesConnectTokenFromLookupResult()
    {
        var resolver = new DevTunnelEndpointResolver(
            new FakeLookupClient(new DevTunnelLookupResult("tunnel-abc", "usw2", [5280], ConnectToken: "api-issued-connect-token")));

        var resolution = await resolver.ResolveAsync("my-tunnel", DevTunnelAccessMode.Private, TestContext.Current.CancellationToken);

        Assert.Equal("api-issued-connect-token", resolution.TunnelAuthToken);
    }

    [Fact]
    public async Task ResolveAsync_PrivateMode_WhenConnectTokenIsNull_Throws()
    {
        var resolver = new DevTunnelEndpointResolver(
            new FakeLookupClient(new DevTunnelLookupResult("tunnel-abc", "usw2", [5280], ConnectToken: null)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("my-tunnel", DevTunnelAccessMode.Private, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_AnonymousMode_ReturnsNullToken()
    {
        var resolver = new DevTunnelEndpointResolver(
            new FakeLookupClient(new DevTunnelLookupResult("tunnel-abc", "usw2", [5280], ConnectToken: "irrelevant-token")));

        var resolution = await resolver.ResolveAsync("my-tunnel", DevTunnelAccessMode.Anonymous, TestContext.Current.CancellationToken);

        Assert.Null(resolution.TunnelAuthToken);
    }

    [Fact]
    public async Task ResolveAsync_TokenMode_UsesConnectTokenFromLookupResult()
    {
        var resolver = new DevTunnelEndpointResolver(
            new FakeLookupClient(new DevTunnelLookupResult("tunnel-abc", "usw2", [5280], ConnectToken: "api-issued-connect-token")));

#pragma warning disable CS0618 // Token is obsolete; kept for migration test coverage
        var resolution = await resolver.ResolveAsync("my-tunnel", DevTunnelAccessMode.Token, TestContext.Current.CancellationToken);
#pragma warning restore CS0618

        Assert.Equal("api-issued-connect-token", resolution.TunnelAuthToken);
    }

    [Fact]
    public async Task ResolveAsync_TokenMode_WhenConnectTokenIsNull_Throws()
    {
        var resolver = new DevTunnelEndpointResolver(
            new FakeLookupClient(new DevTunnelLookupResult("tunnel-abc", "usw2", [5280], ConnectToken: null)));

#pragma warning disable CS0618 // Token is obsolete; kept for migration test coverage
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.ResolveAsync("my-tunnel", DevTunnelAccessMode.Token, TestContext.Current.CancellationToken));
#pragma warning restore CS0618
    }

    [Fact]
    public async Task ResolveAsync_WhenNotExactlyOnePort_Throws()
    {
        var resolverNoPorts = new DevTunnelEndpointResolver(
            new FakeLookupClient(new DevTunnelLookupResult("tunnel-abc", "usw2", [], ConnectToken: "token")));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolverNoPorts.ResolveAsync("my-tunnel", DevTunnelAccessMode.Private, TestContext.Current.CancellationToken));

        var resolverTwoPorts = new DevTunnelEndpointResolver(
            new FakeLookupClient(new DevTunnelLookupResult("tunnel-abc", "usw2", [5280, 6000], ConnectToken: "token")));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolverTwoPorts.ResolveAsync("my-tunnel", DevTunnelAccessMode.Private, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveAsync_WithAutoSelector_DiscoversSingleTunnel_WithoutLookupByName()
    {
        var lookupClient = new FakeLookupClient(new DevTunnelLookupResult("tunnel-auto", "usw2", [5280], ConnectToken: "connect-token"));
        var resolver = new DevTunnelEndpointResolver(lookupClient);

        var resolution = await resolver.ResolveAsync("auto", DevTunnelAccessMode.Private, TestContext.Current.CancellationToken);

        Assert.Equal(new Uri("https://tunnel-auto-5280.usw2.devtunnels.ms/"), resolution.BaseUri);
        Assert.True(lookupClient.DiscoverCalled);
        Assert.Null(lookupClient.LookedUpName);
    }

    [Fact]
    public async Task ResolveAsync_WithBlankName_DiscoversSingleTunnel()
    {
        var lookupClient = new FakeLookupClient(new DevTunnelLookupResult("tunnel-auto", "usw2", [5280], ConnectToken: "connect-token"));
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
