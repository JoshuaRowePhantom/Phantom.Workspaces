using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.IntegrationTests;

[Collection("DevTunnel")]
public sealed class DevTunnelIntegrationTests(InProcessDevTunnelPrivateFixture fixture) :
    IClassFixture<InProcessDevTunnelPrivateFixture>
{
    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task DevTunnelPrivateConnect_OwnerSameIdentity_UsesBrokerConnectLease()
    {
        var resolver = new DevTunnelEndpointResolver(fixture.Access!.Broker, fixture.ConnectRequest!);
        var resolution = await resolver.ResolveAsync(fixture.TunnelName!, DevTunnelAccessMode.Private,
            TestContext.Current.CancellationToken);
        Assert.Equal(fixture.RelayBaseUri, resolution.BaseUri);
        Assert.Null(resolution.GetType().GetProperty("TunnelAuthToken"));
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync(resolution.BaseUri, TestContext.Current.CancellationToken);
        Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }
}
