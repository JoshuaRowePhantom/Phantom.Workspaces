using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests.Access;

public sealed class AccessMigrationTests
{
    [Theory]
    [InlineData("IDevTunnelAuthTokenProvider")]
    [InlineData("IDevTunnelConnectTokenProvider")]
    [InlineData("DevTunnelAuthTokenProviderFactory")]
    [InlineData("DevTunnelAuthProviderDependencies")]
    [InlineData("GitHubDevTunnelAuthTokenProvider")]
    [InlineData("EntraDevTunnelAuthProvider")]
    [InlineData("AnonymousDevTunnelAuthProvider")]
    [InlineData("OAuthDevTunnelAuthProvider")]
    [InlineData("ManagementApiDevTunnelConnectTokenProvider")]
    [InlineData("DelegateDevTunnelConnectTokenProvider")]
    public void AccessMigration_DevTunnelTree_HasNoParallelRawCredentialProvider(string type)
    {
        Assert.Null(typeof(DevTunnelAuthenticationHandler).Assembly.GetType(
            "Phantom.Workspaces.Services.DevTunnel." + type));
    }

    [Fact]
    public void AccessMigration_DevTunnelHostStatus_HasNoConnectMaterial()
    {
        Assert.Null(typeof(DevTunnelHostStatus).GetProperty("ConnectToken"));
    }
}
