using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Web.Client;

namespace Phantom.Workspaces.Data.Web.Client.Tests;

public sealed class WebClientDataAccessLayerTests
{
    [Fact]
    public void IsConnectivityFailure_WithUnauthorizedStatusCode_ReturnsTrue()
    {
        var exception = new WebDataAccessRequestException("401 response", HttpStatusCode.Unauthorized);

        Assert.True(exception.IsConnectivityFailure);
    }

    [Fact]
    public void WebDataAccessRequestException_IsConnectivityFailure_Covers404And503()
    {
        Assert.True(new WebDataAccessRequestException("404 response", HttpStatusCode.NotFound).IsConnectivityFailure);
        Assert.True(new WebDataAccessRequestException("503 response", HttpStatusCode.ServiceUnavailable).IsConnectivityFailure);

        // A genuine application-level 4xx (other than 401/404) is not a connectivity failure.
        Assert.False(new WebDataAccessRequestException("400 response", HttpStatusCode.BadRequest).IsConnectivityFailure);
    }

    [Fact]
    public async Task WebClientDataAccessLayer_UsesSharedAuthenticatedClient_NoOwnHeaderOrRetry()
    {
        // Issue #1456: the client consumes an already-authenticated HttpClient. It must not add an
        // X-Tunnel-Authorization header itself, and it must not retry on 401 — the shared
        // DevTunnelAuthenticationHandler owns both. Here the client is given a plain HttpClient.
        var callCount = 0;
        var sawTunnelHeader = false;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            callCount++;
            if (request.Headers.Contains("X-Tunnel-Authorization"))
            {
                sawTunnelHeader = true;
            }

            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test"),
        };
        using var dataAccessLayer = new WebClientDataAccessLayer("https://example.test", httpClient);

        var exception = await Assert.ThrowsAsync<WebDataAccessRequestException>(
            () => dataAccessLayer.GetAsync(new GetRequest { Entities = [] }));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.False(sawTunnelHeader); // client attaches no auth header of its own
        Assert.Equal(1, callCount);    // no internal retry — a single request is made
    }

    [Fact]
    public async Task UpdateAsync_PostsToExpectedEndpointAndParsesResponse()
    {
        var updateResult = new UpdateResult
        {
            EntityResults = [],
        };
        var handler = new RecordingHttpMessageHandler(
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("https://example.test/data/update", request.RequestUri!.ToString());
                return JsonResponse(updateResult);
            });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test"),
        };
        using var dataAccessLayer = new WebClientDataAccessLayer("https://example.test", httpClient: httpClient);

        var result = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "test",
                    },
                },
                Changes = [],
            });

        Assert.NotNull(result);
        Assert.Empty(result.EntityResults);
    }

    private static HttpResponseMessage JsonResponse<T>(T value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json"),
        };
    }

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(callback(request));
    }
}
