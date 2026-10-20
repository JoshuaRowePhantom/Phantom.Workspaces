using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Phantom.Workspaces.Llm.Interfaces.Access;

namespace Phantom.Workspaces.Tests.Mcp;

public sealed partial class McpOAuthCredentialAccessExecutorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpOAuthExecutor_Send_KeepsBearerOutOfCallerAndResponseRequests(bool existingAuthorization)
    {
        var tracker = new SessionTracker();
        await using var fixture = new Fixture(createHttpHandler: tracker.Create);
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var result = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await acquisition.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        var content = new CallerContent();
        using var request = new HttpRequestMessage(HttpMethod.Put, fixture.Configuration.Endpoint)
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        if (existingAuthorization)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "caller-owned-header");
        }
        request.Headers.Add("X-Request-Id", "caller-correlation");
        request.Options.Set(new HttpRequestOptionsKey<string>("caller-option"), "caller-value");
        var originalAuthorization = request.Headers.Authorization;
        var outcome = await result.Use.UseAsync(
            cancellation => result.Session.SendAsync(request, cancellation), TestContext.Current.CancellationToken);
        using var response = Assert.IsType<CredentialUseSucceeded<HttpResponseMessage>>(outcome).Result;

        Assert.NotSame(request, tracker.Outgoing);
        Assert.Same(originalAuthorization, request.Headers.Authorization);
        Assert.Null(tracker.Outgoing!.Headers.Authorization);
        Assert.Equal("caller-body", tracker.Body);
        Assert.Equal("caller-correlation", Assert.Single(tracker.Outgoing.Headers.GetValues("X-Request-Id")));
        Assert.Equal(HttpVersion.Version20, tracker.Outgoing.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, tracker.Outgoing.VersionPolicy);
        Assert.True(tracker.Outgoing.Options.TryGetValue(new HttpRequestOptionsKey<string>("caller-option"), out var option));
        Assert.Equal("caller-value", option);
        Assert.NotNull(response.RequestMessage);
        Assert.NotSame(tracker.Outgoing, response.RequestMessage);
        Assert.Null(response.RequestMessage.Headers.Authorization);
        Assert.Equal(request.Method, response.RequestMessage.Method);
        Assert.Equal(request.RequestUri, response.RequestMessage.RequestUri);
        Assert.Equal("caller-body", await response.RequestMessage.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));

        response.RequestMessage.Dispose();
        response.Dispose();
        Assert.Equal(0, content.Disposals);
        Assert.Equal("caller-body", await content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        request.Dispose();
        Assert.Equal(1, content.Disposals);
    }

    [Fact]
    public async Task McpOAuthExecutor_FailedSend_ScrubsOwnedRequestWithoutMutatingCaller()
    {
        var tracker = new SessionTracker { Fail = true };
        await using var fixture = new Fixture(createHttpHandler: tracker.Create);
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var result = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await acquisition.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        var content = new CallerContent();
        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Configuration.Endpoint) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", "caller-owned-header");
        await Assert.ThrowsAsync<HttpRequestException>(async () => await result.Use.UseAsync(
            cancellation => result.Session.SendAsync(request, cancellation), TestContext.Current.CancellationToken));
        Assert.NotSame(request, tracker.Outgoing);
        Assert.NotNull(request.Headers.Authorization);
        Assert.Equal("Basic", request.Headers.Authorization.Scheme);
        Assert.Equal("caller-owned-header", request.Headers.Authorization.Parameter);
        Assert.Null(tracker.Outgoing!.Headers.Authorization);
        Assert.Equal(0, content.Disposals);
    }

    [Fact]
    public async Task McpOAuthExecutor_CancelledSend_ScrubsOwnedRequestAndPreservesCallerContent()
    {
        var tracker = new SessionTracker { Block = true };
        await using var fixture = new Fixture(createHttpHandler: tracker.Create);
        var acquisition = fixture.Broker.AcquireAsync(fixture.Request(), TestContext.Current.CancellationToken);
        await fixture.AllowAsync();
        await using var result = Assert.IsType<AccessSucceeded<McpCredentialUseResult>>(
            await acquisition.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Result;
        var content = new CallerContent();
        using var request = new HttpRequestMessage(HttpMethod.Post, fixture.Configuration.Endpoint) { Content = content };
        using var cancellation = new CancellationTokenSource();
        var sending = result.Use.UseAsync(
            token => result.Session.SendAsync(request, token), cancellation.Token).AsTask();
        await tracker.Sending.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.Null(request.Headers.Authorization);
        Assert.Null(tracker.Outgoing!.Headers.Authorization);
        Assert.NotSame(request, tracker.Outgoing);
        Assert.Equal(0, content.Disposals);
        Assert.Equal("caller-body", await content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private sealed class SessionTracker
    {
        private int handlers;
        internal bool Fail { get; init; }
        internal bool Block { get; init; }
        internal HttpRequestMessage? Outgoing { get; set; }
        internal string? Body { get; set; }
        internal TaskCompletionSource Sending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal HttpMessageHandler Create(Server server) => Interlocked.Increment(ref this.handlers) == 3
            ? new SessionHandler(this)
            : new Handler(server);
    }

    private sealed class SessionHandler(SessionTracker tracker) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            tracker.Outgoing = request;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("private-access", request.Headers.Authorization?.Parameter);
            tracker.Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            tracker.Sending.TrySetResult();
            if (tracker.Fail) { throw new HttpRequestException("Controlled request failure."); }
            if (tracker.Block) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("response-body"),
            };
        }
    }

    private sealed class CallerContent : HttpContent
    {
        private readonly byte[] body = Encoding.UTF8.GetBytes("caller-body");
        internal int Disposals { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(this.body).AsTask();
        protected override bool TryComputeLength(out long length) { length = this.body.Length; return true; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { this.Disposals++; }
            base.Dispose(disposing);
        }
    }
}
