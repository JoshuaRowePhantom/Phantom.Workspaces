using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;
using Moq;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests.Access;

public sealed class DevTunnelConnectManagementApiTests
{
    [Theory]
    [InlineData(DevTunnelIdentityScheme.GitHub, "github", false)]
    [InlineData(DevTunnelIdentityScheme.Entra, "aad", false)]
    [InlineData(DevTunnelIdentityScheme.OAuth, "Bearer", false)]
    [InlineData(DevTunnelIdentityScheme.GitHub, "github", true)]
    [InlineData(DevTunnelIdentityScheme.Entra, "aad", true)]
    [InlineData(DevTunnelIdentityScheme.OAuth, "Bearer", true)]
    public async Task ManagementApi_ActualSdkTransportMintsWithPrivateIdentityAndOwnsPipeline(
        DevTunnelIdentityScheme scheme, string expectedScheme, bool redirectFirst)
    {
        var expiration = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var token = Jwt(expiration);
        var listed = Tunnel(null);
        listed.TunnelId = "tsttstst";
        var minted = Tunnel(token);
        minted.TunnelId = listed.TunnelId;
        var descriptor = Descriptor() with { Endpoint = new Uri("https://tsttstst-8080.euw.devtunnels.ms/") };
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var sends = 0;
        var transport = new SdkTransport(request =>
        {
            Assert.Equal(expectedScheme, request.Headers.Authorization!.Scheme);
            Assert.Equal("private identity", request.Headers.Authorization.Parameter);
            Assert.False(request.Headers.Contains("X-Tunnel-Authorization"));
            sends++;
            if (sends == 1 && redirectFirst)
            {
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    RequestMessage = request,
                    Headers =
                    {
                        Location = new UriBuilder(request.RequestUri!)
                        {
                            Host = "euw.rel.tunnels.api.visualstudio.com",
                        }.Uri,
                    },
                };
            }
            string body;
            if (sends == (redirectFirst ? 2 : 1))
            {
                Assert.DoesNotContain("tokenScopes", request.RequestUri!.Query);
                body = JsonSerializer.Serialize(new { value = new[] { new { value = new[] { listed } } } }, json);
            }
            else
            {
                Assert.Equal(redirectFirst ? 3 : 2, sends);
                Assert.Contains("tokenScopes=connect", request.RequestUri!.Query);
                body = JsonSerializer.Serialize(minted, json);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        });
        var api = new DevTunnelConnectManagementApi(createTransport: () => transport);

        using var result = Assert.IsType<DevTunnelConnectManagementSucceeded>(await api.MintAsync(
            descriptor, scheme, "private identity", TestContext.Current.CancellationToken));

        Assert.Equal(redirectFirst ? 3 : 2, sends);
        Assert.Equal(token, new string(result.Material));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expiration), result.ExpiresOn);
        Assert.Equal(descriptor.Endpoint, result.Endpoint);
        Assert.Equal(1, transport.Disposals);
        Assert.All(transport.Requests, request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-Tunnel-Authorization"));
        });
        Assert.All(transport.Responses, response =>
        {
            Assert.NotNull(response.RequestMessage);
            Assert.Null(response.RequestMessage.Headers.Authorization);
            Assert.False(response.RequestMessage.Headers.Contains("X-Tunnel-Authorization"));
        });
    }

    [Theory]
    [InlineData(DevTunnelIdentityScheme.GitHub, "github")]
    [InlineData(DevTunnelIdentityScheme.Entra, "aad")]
    [InlineData(DevTunnelIdentityScheme.OAuth, "Bearer")]
    public async Task ManagementApi_ActualSdkRedirectNeverSendsPrivateIdentityToRelay(
        DevTunnelIdentityScheme scheme, string expectedScheme)
    {
        var transport = new SdkTransport(request =>
        {
            Assert.EndsWith(".rel.tunnels.api.visualstudio.com", request.RequestUri!.Host);
            Assert.Equal(expectedScheme, request.Headers.Authorization!.Scheme);
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                RequestMessage = request,
                Headers = { Location = Descriptor().Endpoint },
            };
        });
        var api = new DevTunnelConnectManagementApi(createTransport: () => transport);

        var failure = Assert.IsType<DevTunnelConnectManagementFailed>(await api.MintAsync(
            Descriptor(), scheme, "private identity", TestContext.Current.CancellationToken));

        Assert.Equal(AccessErrorCode.IdentityMismatch, failure.ErrorCode);
        Assert.Single(transport.Requests);
        Assert.Equal(1, transport.Disposals);
        Assert.Null(transport.Requests[0].Headers.Authorization);
        Assert.Null(transport.Responses[0].RequestMessage!.Headers.Authorization);
    }

    [Theory]
    [InlineData("https://other.example/")]
    [InlineData("http://global.rel.tunnels.api.visualstudio.com/")]
    [InlineData("https://global.rel.tunnels.api.visualstudio.com:8443/")]
    [InlineData("https://user@global.rel.tunnels.api.visualstudio.com/")]
    [InlineData("https://global.rel.tunnels.api.visualstudio.com/#fragment")]
    public async Task ManagementApi_ActualSdkRedirectRejectsUnsafeDestination(string destination)
    {
        var transport = new SdkTransport(request => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            RequestMessage = request,
            Headers = { Location = new Uri(destination) },
        });
        var api = new DevTunnelConnectManagementApi(createTransport: () => transport);

        var failure = Assert.IsType<DevTunnelConnectManagementFailed>(await api.MintAsync(
            Descriptor(), DevTunnelIdentityScheme.GitHub, "private identity", TestContext.Current.CancellationToken));

        Assert.Equal(AccessErrorCode.IdentityMismatch, failure.ErrorCode);
        Assert.Single(transport.Requests);
        Assert.Equal(1, transport.Disposals);
    }

    private sealed class SdkTransport(Func<HttpRequestMessage, HttpResponseMessage> respond) :
        DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        internal List<HttpRequestMessage> Requests { get; } = [];

        internal List<HttpResponseMessage> Responses { get; } = [];

        internal int Disposals { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            var response = respond(request);
            this.Responses.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.Disposals++;
            }
            base.Dispose(disposing);
        }
    }

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
    public async Task ManagementApi_ListTokensAreIgnored_GetResultMustMintConnectScope()
    {
        var selected = Tunnel(Jwt(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()));
        var fetched = Tunnel(null);
        var management = Client(selected);
        management.Setup(client => client.GetTunnelAsync(selected,
                It.Is<TunnelRequestOptions>(options => options.IncludePorts &&
                    options.TokenScopes.SequenceEqual(new[] { TunnelAccessScopes.Connect })), It.IsAny<CancellationToken>()))
            .ReturnsAsync(fetched);
        Assert.IsType<DevTunnelConnectManagementFailed>(await new DevTunnelConnectManagementApi(_ => management.Object)
            .MintAsync(Descriptor(), DevTunnelIdentityScheme.GitHub, "identity", TestContext.Current.CancellationToken));
        management.VerifyAll();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task ManagementApi_RequiresExactlyOneMatchingWorkspacesTunnel(bool auto, int matchingCount)
    {
        var matches = Enumerable.Range(0, matchingCount).Select(_ => Tunnel(Jwt(
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()))).ToArray();
        var foreign = Tunnel(null);
        foreign.Labels = ["not a Workspaces tunnel"];
        var nonmatching = Tunnel(null);
        nonmatching.Labels = auto ? ["not a Workspaces tunnel"] : [DevTunnelNaming.WorkspacesMarkerLabel, "other tunnel"];
        var management = new Mock<ITunnelManagementClient>(MockBehavior.Strict);
        management.Setup(client => client.DisposeAsync()).Returns(ValueTask.CompletedTask);
        management.Setup(client => client.ListTunnelsAsync(null, null,
                It.Is<TunnelRequestOptions>(options => options.IncludePorts && options.RequireAllLabels &&
                    options.Labels.Contains(DevTunnelNaming.WorkspacesMarkerLabel) &&
                    (options.TokenScopes == null || options.TokenScopes.Length == 0)),
                true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(matches.Concat(new[] { foreign, nonmatching }).ToArray());
        if (matchingCount == 1)
        {
            management.Setup(client => client.GetTunnelAsync(matches[0], It.IsAny<TunnelRequestOptions>(),
                It.IsAny<CancellationToken>())).ReturnsAsync(matches[0]);
        }
        var outcome = await new DevTunnelConnectManagementApi(_ => management.Object).MintAsync(
            Descriptor() with { Name = auto ? "auto" : "selected tunnel" }, DevTunnelIdentityScheme.GitHub,
            "identity", TestContext.Current.CancellationToken);
        if (matchingCount == 1)
        {
            using var success = Assert.IsType<DevTunnelConnectManagementSucceeded>(outcome);
            Assert.Equal(Descriptor().Endpoint, success.Endpoint);
        }
        else
        {
            Assert.IsType<DevTunnelConnectManagementFailed>(outcome);
            management.Verify(client => client.GetTunnelAsync(It.IsAny<Tunnel>(), It.IsAny<TunnelRequestOptions>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [Fact]
    public async Task ManagementApi_ExplicitEndpointSelectsActualTunnelWithoutInventingName()
    {
        var selected = Tunnel(Jwt(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()));
        var other = Tunnel(null);
        other.TunnelId = "other";
        var management = Client(selected);
        management.Setup(client => client.ListTunnelsAsync(null, null, It.IsAny<TunnelRequestOptions>(),
                true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([other, selected]);
        management.Setup(client => client.GetTunnelAsync(selected,
                It.Is<TunnelRequestOptions>(options =>
                    options.TokenScopes.SequenceEqual(new[] { TunnelAccessScopes.Connect })),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(selected);

        using var result = Assert.IsType<DevTunnelConnectManagementSucceeded>(
            await new DevTunnelConnectManagementApi(_ => management.Object).MintAsync(
                Descriptor() with { Name = null }, DevTunnelIdentityScheme.GitHub,
                "identity", TestContext.Current.CancellationToken));

        Assert.Equal(Descriptor().Endpoint, result.Endpoint);
        management.Verify(client => client.GetTunnelAsync(other, It.IsAny<TunnelRequestOptions>(),
            It.IsAny<CancellationToken>()), Times.Never);
        management.Verify(client => client.GetTunnelAsync(selected, It.IsAny<TunnelRequestOptions>(),
            It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task ManagementApi_SdkWrapped401_IsRenewal()
    {
        var tunnel = Tunnel(null);
        var management = Client(tunnel);
        management.Setup(client => client.GetTunnelAsync(tunnel, It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException("unsafe service detail",
                new HttpRequestException("unsafe response", null, HttpStatusCode.Unauthorized)));
        Assert.IsType<DevTunnelConnectManagementUnauthorized>(await new DevTunnelConnectManagementApi(_ => management.Object)
            .MintAsync(Descriptor(), DevTunnelIdentityScheme.GitHub, "identity", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ManagementApi_SdkPermissionAndThrottleErrors_AreFailuresNotRenewal(HttpStatusCode status)
    {
        var tunnel = Tunnel(null);
        var management = Client(tunnel);
        var httpError = new HttpRequestException("unsafe service detail", null, status);
        Exception sdkError = status == HttpStatusCode.Forbidden
            ? new UnauthorizedAccessException("unsafe service detail", httpError)
            : new InvalidOperationException("unsafe service detail", httpError);
        management.Setup(client => client.GetTunnelAsync(tunnel, It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(sdkError);
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
