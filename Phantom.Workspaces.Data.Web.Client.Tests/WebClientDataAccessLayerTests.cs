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
    public async Task GetAsync_On401_WithTokenResolver_RefreshesTokenAndRetries()
    {
        var callCount = 0;
        var observedAuthHeaders = new List<string?>();
        var handler = new RecordingHttpMessageHandler(request =>
        {
            callCount++;
            observedAuthHeaders.Add(
                request.Headers.TryGetValues("X-Tunnel-Authorization", out var vals)
                    ? string.Join(",", vals)
                    : null);
            return callCount == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : JsonResponse(new GetResult { Batches = [] });
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test"),
        };
        using var dataAccessLayer = new WebClientDataAccessLayer(
            "https://example.test",
            devTunnelAccessToken: "old-token",
            devTunnelAccessTokenResolver: () => "new-token",
            httpClient: httpClient);

        var result = await dataAccessLayer.GetAsync(new GetRequest { Entities = [] });

        Assert.NotNull(result);
        Assert.Equal(2, callCount);
        Assert.Equal("tunnel old-token", observedAuthHeaders[0]);
        Assert.Equal("tunnel new-token", observedAuthHeaders[1]);
    }

    [Fact]
    public async Task GetAsync_On401_WithoutTokenResolver_ThrowsWebDataAccessRequestException()
    {
        var handler = new RecordingHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test"),
        };
        using var dataAccessLayer = new WebClientDataAccessLayer(
            "https://example.test",
            devTunnelAccessToken: "token",
            httpClient: httpClient);

        var exception = await Assert.ThrowsAsync<WebDataAccessRequestException>(
            () => dataAccessLayer.GetAsync(new GetRequest { Entities = [] }));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task GetAsync_On401_WithTokenResolver_AfterRetryAlso401_ThrowsWebDataAccessRequestException()
    {
        var callCount = 0;
        var handler = new RecordingHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test"),
        };
        using var dataAccessLayer = new WebClientDataAccessLayer(
            "https://example.test",
            devTunnelAccessToken: "old-token",
            devTunnelAccessTokenResolver: () => "new-token",
            httpClient: httpClient);

        var exception = await Assert.ThrowsAsync<WebDataAccessRequestException>(
            () => dataAccessLayer.GetAsync(new GetRequest { Entities = [] }));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(2, callCount); // initial + one retry
    }

    [Fact]
    public async Task GetAsync_On401_WithTokenResolverReturningNull_RetriesWithoutUpdatingHeader()
    {
        var callCount = 0;
        var handler = new RecordingHttpMessageHandler(_ =>
        {
            callCount++;
            return callCount == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : JsonResponse(new GetResult { Batches = [] });
        });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test"),
        };
        using var dataAccessLayer = new WebClientDataAccessLayer(
            "https://example.test",
            devTunnelAccessToken: "old-token",
            devTunnelAccessTokenResolver: () => null,
            httpClient: httpClient);

        var result = await dataAccessLayer.GetAsync(new GetRequest { Entities = [] });

        Assert.NotNull(result);
        Assert.Equal(2, callCount);
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

    [Fact]
    public async Task Constructor_WithDevTunnelToken_AddsAuthorizationHeader()
    {
        var handler = new RecordingHttpMessageHandler(
            request =>
            {
                Assert.True(request.Headers.TryGetValues("X-Tunnel-Authorization", out var values));
                Assert.Contains("tunnel token-value", values!);
                return JsonResponse(new GetResult
                {
                    Batches = [],
                });
            });
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://example.test"),
        };
        using var dataAccessLayer = new WebClientDataAccessLayer(
            "https://example.test",
            devTunnelAccessToken: "token-value",
            httpClient: httpClient);

        var result = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [],
            });

        Assert.NotNull(result);
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
