using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;
using Moq;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests.Access;

public sealed class DevTunnelConnectManagementApiTests
{
    [Theory]
    [InlineData(DevTunnelIdentityScheme.GitHub, "github")]
    [InlineData(DevTunnelIdentityScheme.Entra, "aad")]
    [InlineData(DevTunnelIdentityScheme.OAuth, "Bearer")]
    public async Task ManagementApi_MintsOnlySelectedTunnel_UsingSchemeAndRealExpiry(
        DevTunnelIdentityScheme scheme, string expectedScheme)
    {
        var expiresOn = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var token = Jwt(expiresOn);
        var tunnel = Tunnel(token);
        var management = Client(tunnel);
        management.Setup(client => client.GetTunnelAsync(tunnel,
                It.Is<TunnelRequestOptions>(options => options.TokenScopes.SequenceEqual(new[] { TunnelAccessScopes.Connect })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(tunnel);
        AuthenticationHeaderValue? suppliedHeader = null;
        var api = new DevTunnelConnectManagementApi(header =>
        {
            suppliedHeader = header;
            return management.Object;
        });

        using var result = Assert.IsType<DevTunnelConnectManagementSucceeded>(await api.MintAsync(
            Descriptor(), scheme, "identity for management only", TestContext.Current.CancellationToken));
        Assert.Equal(expectedScheme, suppliedHeader!.Scheme);
        Assert.Equal("identity for management only", suppliedHeader.Parameter);
        Assert.Equal(token, new string(result.Material));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expiresOn), result.ExpiresOn);
        Assert.Equal(Descriptor().Endpoint, result.Endpoint);
        management.VerifyAll();
    }

    [Fact]
    public async Task ManagementApi_ConfiguredEndpointMismatch_DoesNotMintForAnotherTunnel()
    {
        var management = Client(Tunnel(Jwt(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds())));
        var api = new DevTunnelConnectManagementApi(_ => management.Object);

        Assert.Equal(AccessErrorCode.IdentityMismatch, Assert.IsType<DevTunnelConnectManagementFailed>(
            await api.MintAsync(Descriptor() with { Endpoint = new Uri("https://other-8080.euw.devtunnels.ms/") },
                DevTunnelIdentityScheme.GitHub, "identity", TestContext.Current.CancellationToken)).ErrorCode);
        management.Verify(client => client.GetTunnelAsync(It.IsAny<Tunnel>(), It.IsAny<TunnelRequestOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a JWT")]
    public async Task ManagementApi_MissingOrMalformedConnectToken_FailsClosed(string? token)
    {
        var tunnel = Tunnel(token);
        var management = Client(tunnel);
        management.Setup(client => client.GetTunnelAsync(tunnel, It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tunnel);
        Assert.IsType<DevTunnelConnectManagementFailed>(await new DevTunnelConnectManagementApi(_ => management.Object)
            .MintAsync(Descriptor(), DevTunnelIdentityScheme.GitHub, "identity", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ManagementApi_Management401_IsTypedRenewalNotSilentRetry()
    {
        var tunnel = Tunnel(null);
        var management = Client(tunnel);
        management.Setup(client => client.GetTunnelAsync(tunnel, It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("unsafe provider detail", null, HttpStatusCode.Unauthorized));
        Assert.IsType<DevTunnelConnectManagementUnauthorized>(await new DevTunnelConnectManagementApi(_ => management.Object)
            .MintAsync(Descriptor(), DevTunnelIdentityScheme.GitHub, "identity", TestContext.Current.CancellationToken));
        management.Verify(client => client.GetTunnelAsync(tunnel, It.IsAny<TunnelRequestOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<ITunnelManagementClient> Client(Tunnel tunnel)
    {
        var client = new Mock<ITunnelManagementClient>(MockBehavior.Strict);
        client.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
        client.Setup(value => value.ListTunnelsAsync(null, null,
                It.Is<TunnelRequestOptions>(options => options.TokenScopes == null || options.TokenScopes.Length == 0),
                true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([tunnel]);
        return client;
    }

    private static Tunnel Tunnel(string? token) => new()
    {
        TunnelId = "selected",
        ClusterId = "euw",
        Labels = [DevTunnelNaming.WorkspacesMarkerLabel, "selected tunnel"],
        Ports = [new TunnelPort { PortNumber = 8080 }],
        AccessTokens = token is null ? null : new Dictionary<string, string> { [TunnelAccessScopes.Connect] = token },
    };

    private static DevTunnelAccessDescriptor Descriptor() => new()
    {
        Access = DevTunnelAccessKind.Private,
        Name = "selected tunnel",
        Endpoint = new Uri("https://selected-8080.euw.devtunnels.ms/"),
    };

    private static string Jwt(long expiresOn) =>
        "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"exp\":{expiresOn}}}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
}
