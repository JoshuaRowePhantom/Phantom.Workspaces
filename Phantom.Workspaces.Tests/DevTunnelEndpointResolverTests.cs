using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.DevTunnel;
using Phantom.Workspaces.Tests.Access;

namespace Phantom.Workspaces.Tests;

public sealed class DevTunnelEndpointResolverTests
{
    [Fact]
    public async Task ResolveAsync_FailedEndpointRequiresExplicitRemint_StaleReceiptCannotDeleteReplacement()
    {
        await using var fixture = new DevTunnelConnectCredentialAccessExecutorTests.Fixture(discoverEndpoint: true);
        await using var initial = await fixture.AcquireAsync();
        var resolver = new DevTunnelEndpointResolver(fixture.Broker, fixture.Request);
        var first = await resolver.ResolveAsync(fixture.Request.Tunnel.Name!, DevTunnelAccessMode.Private,
            TestContext.Current.CancellationToken);
        await first.InvalidateForRefreshAsync(TestContext.Current.CancellationToken);
        fixture.Session.Endpoint = new Uri("https://tunnel-456.euw.devtunnels.ms/");

        var pending = resolver.ResolveAsync(fixture.Request.Tunnel.Name!, DevTunnelAccessMode.Private,
            TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, fixture.Session.Mints);
        var approval = fixture.Registry.GetSnapshot().Last(row =>
            row.State is UserAuthorizationState.Pending or UserAuthorizationState.RenewalNeeded);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, approval.PrimaryAction);
        await fixture.ApplyAsync(approval);
        var replacement = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(fixture.Session.Endpoint, replacement.BaseUri);
        Assert.Equal(2, fixture.Session.Mints);

        await first.InvalidateForRefreshAsync(TestContext.Current.CancellationToken);
        var stillCurrent = await resolver.ResolveAsync(fixture.Request.Tunnel.Name!, DevTunnelAccessMode.Private,
            TestContext.Current.CancellationToken);
        Assert.Equal(replacement.BaseUri, stillCurrent.BaseUri);
        Assert.Equal(2, fixture.Session.Mints);
    }

    [Fact]
    public async Task ResolveAsync_CancelledRefreshDoesNotDeleteCurrentGeneration()
    {
        await using var fixture = new DevTunnelConnectCredentialAccessExecutorTests.Fixture();
        await using var initial = await fixture.AcquireAsync();
        var resolver = new DevTunnelEndpointResolver(fixture.Broker, fixture.Request);
        var resolution = await resolver.ResolveAsync(fixture.Request.Tunnel.Name!, DevTunnelAccessMode.Private,
            TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolution.InvalidateForRefreshAsync(cancellation.Token).AsTask());
        await resolver.ResolveAsync(fixture.Request.Tunnel.Name!, DevTunnelAccessMode.Private,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Session.Mints);
    }

    [Fact]
    public async Task ResolveAsync_PresentationCallbackTracksBootstrapThenRuntimeRenewal()
    {
        await using var fixture = new DevTunnelConnectCredentialAccessExecutorTests.Fixture();
        var scope = AccessPresentationScope.Bootstrap;
        var resolver = new DevTunnelEndpointResolver(fixture.Broker, fixture.Request, () => scope);
        var bootstrap = resolver.ResolveAsync(fixture.Request.Tunnel.Name!, DevTunnelAccessMode.Private,
            TestContext.Current.CancellationToken);
        var first = Assert.Single(fixture.Registry.GetSnapshot());
        Assert.True(first.IsBootstrapBlocking);
        await fixture.ApplyAsync(first);
        await bootstrap;
        var cached = Assert.Single(await fixture.Executor.ListAsync(TestContext.Current.CancellationToken));
        await fixture.Executor.DeleteAsync(cached.Identifier, TestContext.Current.CancellationToken);

        scope = AccessPresentationScope.Runtime;
        var runtime = resolver.ResolveAsync(fixture.Request.Tunnel.Name!, DevTunnelAccessMode.Private,
            TestContext.Current.CancellationToken);
        var renewal = fixture.Registry.GetSnapshot().Last(row =>
            row.State is UserAuthorizationState.Pending or UserAuthorizationState.RenewalNeeded);
        Assert.False(renewal.IsBootstrapBlocking);
        await fixture.ApplyAsync(renewal);
        await runtime;
        Assert.Equal(2, fixture.Session.Mints);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsOnlyAuthorizedRelayMetadata_NotConnectMaterial()
    {
        await using var fixture = new DevTunnelConnectCredentialAccessExecutorTests.Fixture();
        await using var initial = await fixture.AcquireAsync();
        var resolver = new DevTunnelEndpointResolver(fixture.Broker, fixture.Request);
        var result = await resolver.ResolveAsync(fixture.Request.Tunnel.Name!, DevTunnelAccessMode.Private,
            TestContext.Current.CancellationToken);
        Assert.Equal(fixture.Session.Endpoint, result.BaseUri);
        Assert.Null(result.GetType().GetProperty("TunnelAuthToken"));
        Assert.Equal(1, fixture.Session.Mints);
    }

    [Fact]
    public async Task ResolveAsync_DifferentTunnelCannotUseAuthorizedDescriptor()
    {
        await using var fixture = new DevTunnelConnectCredentialAccessExecutorTests.Fixture();
        var resolver = new DevTunnelEndpointResolver(fixture.Broker, fixture.Request);
        await Assert.ThrowsAsync<DevTunnelCredentialAccessException>(() =>
            resolver.ResolveAsync("another tunnel", DevTunnelAccessMode.Private, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Registry.GetSnapshot());
        Assert.Equal(0, fixture.Session.Mints);
    }
}
