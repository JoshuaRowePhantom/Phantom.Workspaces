using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class GitHubIdentityResolverTests
{
    [Fact]
    public async Task GetUsernameAsync_ReturnsLoginFromUserEndpoint()
    {
        var callCount = 0;
        var factory = new FakeHttpClientFactory(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"login":"octocat","id":1}"""),
            };
        });

        var resolver = new GitHubIdentityResolver(factory);

        var username = await resolver.GetUsernameAsync("ghs_token123", CancellationToken.None);

        Assert.Equal("octocat", username);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetUsernameAsync_CachesResult_SecondCallDoesNotHitHttp()
    {
        var callCount = 0;
        var factory = new FakeHttpClientFactory(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"login":"octocat"}"""),
            };
        });

        var resolver = new GitHubIdentityResolver(factory);
        var ct = CancellationToken.None;

        var first = await resolver.GetUsernameAsync("ghs_token123", ct);
        var second = await resolver.GetUsernameAsync("ghs_token123", ct);

        Assert.Equal("octocat", first);
        Assert.Equal("octocat", second);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetUsernameAsync_InvalidToken_ReturnsNull()
    {
        var factory = new FakeHttpClientFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var resolver = new GitHubIdentityResolver(factory);

        var username = await resolver.GetUsernameAsync("bad_token", CancellationToken.None);

        Assert.Null(username);
    }

    [Fact]
    public async Task GetUsernameAsync_ResponseMissingLoginField_ReturnsNull()
    {
        var factory = new FakeHttpClientFactory(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":1}"""),
            });

        var resolver = new GitHubIdentityResolver(factory);

        var username = await resolver.GetUsernameAsync("ghs_token", CancellationToken.None);

        Assert.Null(username);
    }

    [Fact]
    public async Task GetUsernameAsync_DifferentTokens_AreResolvedSeparately()
    {
        var factory = new FakeHttpClientFactory(req =>
        {
            var auth = req.Headers.Authorization?.Parameter ?? "";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"login":"user-{{auth}}"}"""),
            };
        });

        var resolver = new GitHubIdentityResolver(factory);
        var ct = CancellationToken.None;

        var first = await resolver.GetUsernameAsync("token-a", ct);
        var second = await resolver.GetUsernameAsync("token-b", ct);

        Assert.Equal("user-token-a", first);
        Assert.Equal("user-token-b", second);
    }

    private sealed class FakeHttpClientFactory(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new StubHandler(handler));

        private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromResult(handler(request));
        }
    }
}
