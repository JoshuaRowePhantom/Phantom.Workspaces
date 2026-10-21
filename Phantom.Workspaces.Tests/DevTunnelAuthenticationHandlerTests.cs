using System.Net;
using System.Net.Http;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.DevTunnel;
using Phantom.Workspaces.Tests.Access;

namespace Phantom.Workspaces.Tests;

public sealed class DevTunnelAuthenticationHandlerTests
{
    [Theory]
    [InlineData(AccessPresentationScope.Bootstrap, true)]
    [InlineData(AccessPresentationScope.Runtime, false)]
    public async Task DevTunnelAuthenticationHandler_UsesCurrentPresentationScopeBeforeRelease(
        AccessPresentationScope scope, bool bootstrap)
    {
        await using var fixture = new DevTunnelConnectCredentialAccessExecutorTests.Fixture();
        using var client = new AuthenticatedHttpClientFactory(() => new RecordingHandler(_ =>
            throw new InvalidOperationException("Unapproved request reached transport.")))
            .CreateClient(fixture.Session.Endpoint, fixture.Broker, fixture.Request, () => scope);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = client.GetAsync(fixture.Session.Endpoint, cancellation.Token);
        Assert.Equal(bootstrap, Assert.Single(fixture.Registry.GetSnapshot()).IsBootstrapBlocking);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, fixture.Session.Mints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevTunnelClientAuthorization_IdentityToken_NeverReachesRelay(bool returnsCallerRequest)
    {
        await using var fixture = new DevTunnelConnectCredentialAccessExecutorTests.Fixture();
        await using var initial = await fixture.AcquireAsync();
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(fixture.Session.Endpoint, "data"));
        message.Headers.Authorization = new("Bearer", "identity never allowed");
        message.Headers.TryAddWithoutValidation("X-Tunnel-Authorization", "caller supplied header");
        var calls = 0;
        using var client = Client(fixture, request =>
        {
            calls++;
            Assert.NotSame(message, request);
            Assert.Equal("identity never allowed", message.Headers.Authorization!.Parameter);
            Assert.Equal("caller supplied header", Header(message));
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("tunnel private connect material", Header(request));
            if (returnsCallerRequest)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = message };
            }
            var sent = new HttpRequestMessage(request.Method, request.RequestUri);
            sent.Headers.TryAddWithoutValidation("X-Tunnel-Authorization", Header(request));
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = sent };
        });
        using var response = await client.SendAsync(message, TestContext.Current.CancellationToken);
        using var sent = response.RequestMessage;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, calls);
        Assert.Equal("caller supplied header", Header(message));
        Assert.Equal("identity never allowed", message.Headers.Authorization!.Parameter);
        Assert.NotNull(sent);
        Assert.NotSame(message, sent);
        Assert.Null(Header(sent));
        Assert.Null(sent.Headers.Authorization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevTunnelAuthenticationHandler_FailedSendPreservesCallerAndScrubsOwnedRequest(bool cancelled)
    {
        await using var fixture = new DevTunnelConnectCredentialAccessExecutorTests.Fixture();
        await using var initial = await fixture.AcquireAsync();
        using var message = new HttpRequestMessage(HttpMethod.Get, fixture.Session.Endpoint);
        message.Headers.Authorization = new("Bearer", "caller identity");
        message.Headers.TryAddWithoutValidation("X-Tunnel-Authorization", "caller supplied header");
        HttpRequestMessage? outgoing = null;
        using var client = Client(fixture, request =>
        {
            outgoing = request;
            Assert.NotSame(message, request);
            Assert.Equal("caller identity", message.Headers.Authorization!.Parameter);
            Assert.Equal("caller supplied header", Header(message));
            Assert.Equal("tunnel private connect material", Header(request));
            Exception failure = cancelled
                ? new OperationCanceledException(new CancellationToken(true))
                : new HttpRequestException("transport failed");
            throw failure;
        });

        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.SendAsync(message, TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.SendAsync(message, TestContext.Current.CancellationToken));
        }
        Assert.Equal("caller identity", message.Headers.Authorization!.Parameter);
        Assert.Equal("caller supplied header", Header(message));
        Assert.NotNull(outgoing);
        Assert.Null(Header(outgoing));
        Assert.Null(outgoing.Headers.Authorization);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DevTunnelAuthenticationHandler_Relay401_RenewsAndReplaysOnce(bool rejectAgain)
    {
        await using var fixture = new DevTunnelConnectCredentialAccessExecutorTests.Fixture();
        await using var initial = await fixture.AcquireAsync();
        var calls = 0;
        using var client = Client(fixture, request =>
        {
            calls++;
            Assert.Equal("tunnel private connect material", Header(request));
            return new HttpResponseMessage(calls == 1 || rejectAgain ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
        });
        var pending = client.GetAsync(new Uri(fixture.Session.Endpoint, "data"), TestContext.Current.CancellationToken);
        var row = fixture.Registry.GetSnapshot().Last(snapshot => snapshot.State is UserAuthorizationState.Pending or UserAuthorizationState.RenewalNeeded);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, calls);
        Assert.Equal(1, fixture.Session.Mints);
        Assert.Equal(UserAuthorizationPrimaryActionKind.Perform, row.PrimaryAction);
        await fixture.ApplyAsync(row);
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(rejectAgain ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls);
        Assert.Equal(2, fixture.Session.Mints);
        Assert.DoesNotContain(fixture.Registry.GetSnapshot(), snapshot =>
            snapshot.State is UserAuthorizationState.Pending or UserAuthorizationState.RenewalNeeded);
    }

    [Fact]
    public async Task DevTunnelAuthenticationHandler_CancelledReplayInvalidationDisposesUnpublishedResponse()
    {
        await using var fixture = new DevTunnelConnectCredentialAccessExecutorTests.Fixture();
        await using var initial = await fixture.AcquireAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var content = new TrackedContent();
        var sends = 0;
        using var client = Client(fixture, _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (++sends == 2)
            {
                response.Content = content;
                cancellation.Cancel();
            }
            return response;
        });
        var sending = client.GetAsync(fixture.Session.Endpoint, cancellation.Token);
        await fixture.ApplyAsync(fixture.Registry.GetSnapshot().Last(snapshot =>
            snapshot.State is UserAuthorizationState.Pending or UserAuthorizationState.RenewalNeeded));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(2, sends);
        Assert.True(content.Disposed);
    }

    private sealed class TrackedContent() : StringContent("response")
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            this.Disposed |= disposing;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task DevTunnelAuthenticationHandler_NonAuthFailure_DoesNotRenew()
    {
        await using var fixture = new DevTunnelConnectCredentialAccessExecutorTests.Fixture();
        await using var initial = await fixture.AcquireAsync();
        using var client = Client(fixture, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var response = await client.GetAsync(fixture.Session.Endpoint, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, fixture.Session.Mints);
        Assert.Single(fixture.Registry.GetSnapshot());
    }

    [Fact]
    public async Task DevTunnelAuthenticationHandler_DifferentDestination_NeverSendsCredential()
    {
        await using var fixture = new DevTunnelConnectCredentialAccessExecutorTests.Fixture();
        using var client = Client(fixture, _ => throw new InvalidOperationException("Unauthorized destination reached transport."));
        await Assert.ThrowsAsync<DevTunnelCredentialAccessException>(() =>
            client.GetAsync("https://other-123.euw.devtunnels.ms/", TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Registry.GetSnapshot());
        Assert.Equal(0, fixture.Session.Mints);
    }

    [Fact]
    public async Task DevTunnelAuthenticationHandler_Anonymous401_DoesNotPromptOrReplay()
    {
        await using var fixture = new DevTunnelConnectCredentialAccessExecutorTests.Fixture(anonymous: true);
        await using var initial = await fixture.AcquireAsync();
        var calls = 0;
        using var client = Client(fixture, request =>
        {
            calls++;
            Assert.Null(Header(request));
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        using var response = await client.GetAsync(fixture.Session.Endpoint, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, calls);
        Assert.Single(fixture.Registry.GetSnapshot());
        Assert.Equal(0, fixture.Session.Mints);
    }

    private static HttpClient Client(
        DevTunnelConnectCredentialAccessExecutorTests.Fixture fixture,
        Func<HttpRequestMessage, HttpResponseMessage> callback) =>
        new(new DevTunnelAuthenticationHandler(fixture.Broker, fixture.Request, new RecordingHandler(callback)));

    private static string? Header(HttpRequestMessage request) =>
        request.Headers.TryGetValues("X-Tunnel-Authorization", out var values) ? string.Join(",", values) : null;

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }
}
