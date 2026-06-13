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
