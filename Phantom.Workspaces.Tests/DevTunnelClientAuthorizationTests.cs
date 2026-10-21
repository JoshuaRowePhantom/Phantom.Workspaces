using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests;

public sealed class DevTunnelClientAuthorizationTests
{
    [Theory]
    [InlineData("https://tunnel.euw.devtunnels.ms/data", true)]
    [InlineData("https://other.euw.devtunnels.ms/", false)]
    [InlineData("http://tunnel.euw.devtunnels.ms/", false)]
    [InlineData("https://tunnel.euw.devtunnels.ms:444/", false)]
    [InlineData("https://identity@tunnel.euw.devtunnels.ms/", false)]
    public async Task ConnectAuthorization_BindsExactRelayAuthority(string destination, bool allowed)
    {
        await using var result = new Phantom.Workspaces.Llm.Interfaces.Access.DevTunnelConnectCredentialAccessResult(
            new Uri("https://tunnel.euw.devtunnels.ms/"));
        if (allowed)
        {
            DevTunnelClientAuthorization.ValidateEndpoint(new Uri(destination), result);
        }
        else
        {
            Assert.Throws<DevTunnelCredentialAccessException>(() =>
                DevTunnelClientAuthorization.ValidateEndpoint(new Uri(destination), result));
        }
    }
}
