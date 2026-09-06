using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Services.DevTunnel;
using Xunit;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Issue #1456: verifies the shared <see cref="DevTunnelAuthenticationHandler"/> centralizes the
/// dev-tunnel auth that was previously hand-rolled and duplicated inside the two web clients — attaching
/// the tunnel-authorization header per request, re-minting and retrying once on a relay 401, coalescing
/// concurrent 401s into a single refresh episode, and never re-authenticating on non-auth failures.
/// </summary>
public sealed class DevTunnelAuthenticationHandlerTests
{
    [Fact]
    public async Task DevTunnelAuthenticationHandler_AttachesTunnelAuthorizationHeader_FromProvider()
    {
        var provider = new FakeConnectTokenProvider("token-abc");
        var observedHeaders = new List<string?>();
        var inner = new RecordingHandler(request =>
        {
            observedHeaders.Add(HeaderOrNull(request));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = CreateClient(provider, inner);

        var response = await client.GetAsync("https://relay.test/data/get", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(observedHeaders);
        Assert.Equal("tunnel token-abc", observedHeaders[0]);
        Assert.Equal(0, provider.RefreshCount);
    }

    [Fact]
    public async Task DevTunnelAuthenticationHandler_On401_ReMintsAndRetriesOnce()
    {
        var provider = new FakeConnectTokenProvider("stale-token");
        var callCount = 0;
        var observedHeaders = new List<string?>();
        var inner = new RecordingHandler(request =>
        {
            callCount++;
            observedHeaders.Add(HeaderOrNull(request));
            return callCount == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = CreateClient(provider, inner);

        var response = await client.GetAsync("https://relay.test/data/get", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, callCount);                       // initial + exactly one retry
        Assert.Equal(1, provider.RefreshCount);           // re-minted once
        Assert.Equal("tunnel stale-token", observedHeaders[0]);
        Assert.Equal("tunnel fresh-1", observedHeaders[1]); // retry carries the fresh token
    }

    [Fact]
    public async Task DevTunnelAuthenticationHandler_Concurrent401s_CoalesceIntoSingleRefresh()
    {
        var provider = new FakeConnectTokenProvider("stale-token");
        var inner = new RecordingHandler(request =>
        {
            // Every request whose token is the original stale token gets a 401; the fresh token succeeds.
            return HeaderOrNull(request) == "tunnel stale-token"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = CreateClient(provider, inner);

        var tasks = new List<Task<HttpResponseMessage>>();
        for (var i = 0; i < 8; i++)
        {
            tasks.Add(client.GetAsync("https://relay.test/data/get", TestContext.Current.CancellationToken));
        }

        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(1, provider.RefreshCount); // all concurrent 401s share exactly one refresh episode
    }

    [Fact]
    public async Task DevTunnelAuthenticationHandler_NonAuthFailure_DoesNotRefresh()
    {
        var provider = new FakeConnectTokenProvider("token-abc");
        var callCount = 0;
        var inner = new RecordingHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        using var client = CreateClient(provider, inner);

        var response = await client.GetAsync("https://relay.test/data/get", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, callCount);            // 5xx returned verbatim — no retry
        Assert.Equal(0, provider.RefreshCount); // only auth (401) drives refresh
    }

    private static HttpClient CreateClient(IDevTunnelConnectTokenProvider provider, HttpMessageHandler inner)
        => new(new DevTunnelAuthenticationHandler(provider, inner));

    private static string? HeaderOrNull(HttpRequestMessage request)
        => request.Headers.TryGetValues("X-Tunnel-Authorization", out var values)
            ? string.Join(",", values)
            : null;

    private sealed class FakeConnectTokenProvider(string initialToken) : IDevTunnelConnectTokenProvider
    {
        private readonly object gate = new();
        private string? current = initialToken;
        private int refreshCount;

        public int RefreshCount => Volatile.Read(ref this.refreshCount);

        public ValueTask<string?> GetConnectTokenAsync(CancellationToken cancellationToken = default)
        {
            lock (this.gate)
            {
                return new ValueTask<string?>(this.current);
            }
        }

        public ValueTask<string?> RefreshConnectTokenAsync(CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref this.refreshCount);
            lock (this.gate)
            {
                this.current = $"fresh-{count}";
                return new ValueTask<string?>(this.current);
            }
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(callback(request));
    }
}
