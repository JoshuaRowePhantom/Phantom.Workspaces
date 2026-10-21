using System.Net;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.IntegrationTests;

[Collection("DevTunnel")]
public sealed class DevTunnelAccessControlTests(InProcessDevTunnelPrivateFixture fixture) :
    IClassFixture<InProcessDevTunnelPrivateFixture>
{
    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task PrivateMode_BrokerMintsConnectLease_WithoutHostStatusExposure()
    {
        Assert.Null(typeof(DevTunnelHostStatus).GetProperty("ConnectToken"));
        var outcome = await fixture.Access!.Broker.AcquireAsync(fixture.ConnectRequest!, TestContext.Current.CancellationToken);
        await using var result = Assert.IsType<AccessSucceeded<DevTunnelConnectCredentialAccessResult>>(outcome).Result;
        Assert.NotNull(result.ConnectToken);
        Assert.Equal(fixture.RelayBaseUri, result.Endpoint);
    }

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task PrivateMode_ConnectWithBrokerIssuedConnectLease_Succeeds()
    {
        using var client = fixture.CreateAuthenticatedClient();
        using var response = await client.GetAsync(fixture.RelayBaseUri, TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task PrivateMode_ConnectWithNoToken_Returns401OrForbidden()
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var response = await client.GetAsync(fixture.RelayBaseUri, TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [IntegrationFact(Timeout = 60_000)]
    [Trait("Category", "Integration")]
    public async Task PrivateMode_InvalidConnectMaterial_Returns401OrForbidden()
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, fixture.RelayBaseUri);
        request.Headers.TryAddWithoutValidation("X-Tunnel-Authorization", "tunnel invalid-test-material");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }
}
