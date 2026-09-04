using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Azure.Core;
using Phantom.Workspaces.Llm.Mcp;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Covers <see cref="EntraBearerTokenHandler"/> (#1420): the bearer is attached only to the exact
/// configured HTTPS origin, never to a different origin, and a cross-origin redirect target never
/// receives it.
/// </summary>
public sealed class EntraBearerTokenHandlerTests
{
    private const string Origin = "https://mcp.entra.test";

    [Fact]
    public async Task EntraBearerTokenHandler_RequestToConfiguredOrigin_AttachesBearer()
    {
        var capturing = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = CreateInvoker(capturing, Origin);

        using var request = new HttpRequestMessage(HttpMethod.Get, Origin + "/mcp/");
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        var auth = capturing.Requests.Single().Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal("access-token", auth.Parameter);
    }

    [Theory]
    [InlineData("https://other.entra.test/mcp/")]   // different host
    [InlineData("https://mcp.entra.test:8443/mcp/")] // different port
    [InlineData("http://mcp.entra.test/mcp/")]        // different scheme
    public async Task EntraBearerTokenHandler_RequestToDifferentOrigin_DoesNotAttachBearer(string url)
    {
        var capturing = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var invoker = CreateInvoker(capturing, Origin);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        Assert.Null(capturing.Requests.Single().Authorization);
    }

    [Fact]
    public async Task EntraBearerTokenHandler_CrossOriginRedirect_DoesNotForwardBearer()
    {
        // The pinned origin returns a 302 to a different origin. Because auto-redirect is disabled the
        // handler returns the 3xx rather than following it, and — crucially — a subsequent request to
        // the redirect target origin receives no bearer (the origin is re-checked on every hop).
        var redirectTarget = "https://evil.entra.test/steal";
        var capturing = new CapturingHandler(request =>
        {
            if (request.RequestUri!.GetLeftPart(UriPartial.Authority) == Origin)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri(redirectTarget);
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var invoker = CreateInvoker(capturing, Origin);

        using var initial = new HttpRequestMessage(HttpMethod.Get, Origin + "/mcp/");
        using var redirectResponse = await invoker.SendAsync(initial, CancellationToken.None);

        // The handler did not auto-follow the redirect.
        Assert.Equal(HttpStatusCode.Found, redirectResponse.StatusCode);
        Assert.Equal(new Uri(redirectTarget), redirectResponse.Headers.Location);

        // The bearer went only to the pinned origin.
        var firstHop = capturing.Requests.Single();
        Assert.NotNull(firstHop.Authorization);

        // Manually following the redirect to the different origin carries no bearer.
        using var followed = new HttpRequestMessage(HttpMethod.Get, redirectTarget);
        using var followedResponse = await invoker.SendAsync(followed, CancellationToken.None);

        Assert.Null(capturing.Requests.Last().Authorization);
    }

    private static HttpMessageInvoker CreateInvoker(CapturingHandler inner, string origin)
    {
        var credential = new StubTokenCredential("access-token", DateTimeOffset.UtcNow.AddHours(1));
        var provider = new EntraPinnedTokenProvider(credential, ["api://example/.default"]);
        var handler = new EntraBearerTokenHandler(provider, new Uri(origin))
        {
            InnerHandler = inner,
        };
        return new HttpMessageInvoker(handler);
    }

    private sealed record CapturedRequest(Uri? Uri, AuthenticationHeaderValue? Authorization);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

        public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => this.responder = responder;

        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(new CapturedRequest(request.RequestUri, request.Headers.Authorization));
            return Task.FromResult(this.responder(request));
        }
    }

    private sealed class StubTokenCredential : TokenCredential
    {
        private readonly AccessToken token;

        public StubTokenCredential(string token, DateTimeOffset expiresOn)
            => this.token = new AccessToken(token, expiresOn);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => this.token;

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(this.token);
    }
}
