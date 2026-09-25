using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Tests;

public sealed class WorkspacesTransportHostTests
{
    [Fact]
    public async Task StartAsync_RegistersWithConfiguredHubs()
    {
        var httpA = new FakeHubHttpTransportFactory();
        var httpB = new FakeHubHttpTransportFactory();
        var factoryA = new ReverseHttpClientTransportFactory(httpA, "https://hub-a.example", "machine-a");
        var factoryB = new ReverseHttpClientTransportFactory(httpB, "https://hub-b.example", "machine-a");
        var registry = new TransportRegistry();

        await using var host = new WorkspacesTransportHost(registry, [factoryA, factoryB], Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await host.StartAsync(Ct());

        Assert.Equal(["https://hub-a.example"], factoryA.HubUrls);
        Assert.Equal(["https://hub-b.example"], factoryB.HubUrls);
        Assert.Equal("reverse-register", httpA.Channels.Single().RegisterRequest.GetProperty("type").GetString());
        Assert.Equal("machine-a", httpA.Channels.Single().RegisterRequest.GetProperty("entity-id").GetString());
        Assert.Single(httpB.Channels);
    }

    [Fact]
    public async Task RelayedChannelOpen_DispatchesToLocalChatListener()
    {
        var http = new FakeHubHttpTransportFactory();
        var factory = new ReverseHttpClientTransportFactory(http, "https://hub.example", "machine-a");
        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(new EchoChatClient()));

        await using var host = new WorkspacesTransportHost(registry, [factory], Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await host.StartAsync(Ct());

        var channel = http.Channels.Single();
        await channel.DeliverInbound(Json("""{"type":"channel-open","channelId":"ch1","request":{"type":"chat-client"}}"""));
        await channel.DeliverInbound(Json("""{"type":"channel-message","channelId":"ch1","payload":{"type":"process-streaming","content":{"role":"user","text":"hello"}}}"""));

        var update = await channel.ReadOutbound(Ct());

        Assert.Equal("channel-message", update.GetProperty("type").GetString());
        Assert.Equal("ch1", update.GetProperty("channelId").GetString());
        Assert.Equal("streaming-update", update.GetProperty("payload").GetProperty("type").GetString());
    }

    [Fact]
    public async Task RelayedStreamOpen_DispatchesToLocalShellListener()
    {
        var http = new FakeHubHttpTransportFactory();
        var factory = new ReverseHttpClientTransportFactory(http, "https://hub.example", "machine-a");
        var registry = new TransportRegistry();

        // The shell path is stream-based (ShellTransportListener.OnStreamOpenAsync). A recording
        // stream listener stands in for the shell listener so the test stays hermetic (no real
        // process launch); it asserts the host wires the dispatcher to the local registry and that
        // a relayed stream-open frame reaches a stream listener. Real ShellTransportListener process
        // handling is covered by ShellTransportListenerTests.
        var streamRequest = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Register(new RecordingShellStreamListener(request => streamRequest.TrySetResult(request)));

        await using var host = new WorkspacesTransportHost(registry, [factory], Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        await host.StartAsync(Ct());

        var channel = http.Channels.Single();
        await channel.DeliverInbound(Json("""{"type":"stream-open","streamId":"sh1","request":{"type":"shell","command":"echo"}}"""));

        var request = await streamRequest.Task.WaitAsync(Ct());
        Assert.Equal("shell", request.GetProperty("type").GetString());
        Assert.Equal("echo", request.GetProperty("command").GetString());
    }

    [Fact]
    public async Task RegistrationChannelLost_ReconnectsViaReconnectAsync()
    {
        var http = new FakeHubHttpTransportFactory();
        var factory = new ReverseHttpClientTransportFactory(http, "https://hub.example", "machine-a");
        var registry = new TransportRegistry();

        var states = Channel.CreateUnbounded<bool>();

        await using var host = new WorkspacesTransportHost(registry, [factory], Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        host.ConnectionStateChanged += (_, _) => states.Writer.TryWrite(host.IsConnected);

        await host.StartAsync(Ct());
        Assert.True(await states.Reader.ReadAsync(Ct()));

        var firstChannel = http.Channels.Single();

        // Simulate loss of the registration channel; the host should reconnect via ReconnectAsync,
        // which opens a fresh registration channel through the (fake) HTTP transport factory.
        firstChannel.CompleteInbound();

        Assert.False(await states.Reader.ReadAsync(Ct()));
        Assert.True(await states.Reader.ReadAsync(Ct()));

        Assert.Equal(2, http.Channels.Count);
        Assert.NotSame(firstChannel, http.Channels[^1]);
    }

    [Fact]
    public async Task RegistrationChannelFaults_ReconnectsAndDisposesOldRegistration()
    {
        var http = new FakeHubHttpTransportFactory();
        var factory = new ReverseHttpClientTransportFactory(http, "https://hub.example", "machine-a");
        var states = Channel.CreateUnbounded<bool>();
        await using var host = new WorkspacesTransportHost(
            new TransportRegistry(), [factory], Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        host.ConnectionStateChanged += (_, _) => states.Writer.TryWrite(host.IsConnected);
        await host.StartAsync(Ct());
        Assert.True(await states.Reader.ReadAsync(Ct()));
        var first = Assert.Single(http.Channels);

        first.CompleteInbound(new IOException("private detail"));

        Assert.False(await states.Reader.ReadAsync(Ct()));
        Assert.True(await states.Reader.ReadAsync(Ct()));
        Assert.Equal(2, http.Channels.Count);
        Assert.True(first.Disposed);
        Assert.True(host.IsConnected);
    }

    [Fact]
    public async Task RegistrationInfoHandlerFailsWithOpenChannel_ReconnectsAndDispatchesRelayedChannelOpen()
    {
        var profile = new EntityId("11111111-1111-4111-8111-111111111111");
        var oldHub = new EntityId("22222222-2222-4222-8222-222222222222");
        var newHub = new EntityId("33333333-3333-4333-8333-333333333333");
        var store = new RecordingRouteStore();
        var http = new FakeHubHttpTransportFactory();
        var factory = new ReverseHttpClientTransportFactory(
            http, "https://hub.example", profile.ToString(), store, oldHub);
        var identities = new TransportPeerIdentityProvider();
        var opened = Channel.CreateUnbounded<string>();
        var disposed = Channel.CreateUnbounded<string>();
        var registry = new TransportRegistry();
        registry.Register(new RecordingIdentityChannelListener(
            identities, id => opened.Writer.TryWrite(id), id => disposed.Writer.TryWrite(id)));
        var states = Channel.CreateUnbounded<bool>();
        await using var host = new WorkspacesTransportHost(
            registry, [factory], Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, identities);
        host.ConnectionStateChanged += (_, _) => states.Writer.TryWrite(host.IsConnected);
        await host.StartAsync(Ct());
        Assert.True(await states.Reader.ReadAsync(Ct()));
        Assert.Equal($"upsert:reverse-http:{oldHub}", await store.Operations.ReadAsync(Ct()));

        var first = Assert.Single(http.Channels);
        await first.DeliverInbound(Json("""
            {"type":"channel-open","channelId":"old","authenticatedPeer":{"authenticationScheme":"test","stablePeerId":"old-peer"},
             "request":{"type":"identity"}}
            """));
        Assert.Equal("old-peer", await opened.Reader.ReadAsync(Ct()));

        store.FailNextUpsert();
        await first.DeliverInbound(Json($$"""
            {"type":"reverse-registration-info","hub-profile-entity-id":"{{newHub}}"}
            """));

        Assert.False(await states.Reader.ReadAsync(Ct()));
        Assert.True(first.Disposed);
        Assert.True(first.Reader.Completion.IsCompleted);
        Assert.False(first.CanDeliverInbound(Json("""{"type":"channel-open","channelId":"stale","request":{"type":"identity"}}""")));
        Assert.Equal("old-peer", await disposed.Reader.ReadAsync(Ct()));
        Assert.Equal($"remove:reverse-http:{oldHub}", await store.Operations.ReadAsync(Ct()));
        Assert.Equal($"failed-upsert:reverse-http:{newHub}", await store.Operations.ReadAsync(Ct()));
        Assert.Equal($"remove:reverse-http:{newHub}", await store.Operations.ReadAsync(Ct()));

        Assert.True(await states.Reader.ReadAsync(Ct()));
        var second = Assert.Single(http.Channels.Skip(1));
        Assert.NotSame(first, second);
        Assert.Equal($"upsert:reverse-http:{newHub}", await store.Operations.ReadAsync(Ct()));
        await second.DeliverInbound(Json("""
            {"type":"channel-open","channelId":"new","authenticatedPeer":{"authenticationScheme":"test","stablePeerId":"new-peer"},
             "request":{"type":"identity"}}
            """));
        Assert.Equal("new-peer", await opened.Reader.ReadAsync(Ct()));
        Assert.False(disposed.Reader.TryRead(out _));
        Assert.Null(host.LastRegistrationFailureType);
    }

    [Fact]
    public async Task DispatcherAndRegistrationChannelEndTogether_ReconnectsOnceAndDisposesSessions()
    {
        var http = new FakeHubHttpTransportFactory();
        var factory = new ReverseHttpClientTransportFactory(http, "https://hub.example", "machine-a");
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new TransportRegistry();
        registry.Register(new RecordingChannelListener(() => opened.TrySetResult(), () => disposed.TrySetResult()));
        var states = Channel.CreateUnbounded<bool>();
        await using var host = new WorkspacesTransportHost(registry, [factory], Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        host.ConnectionStateChanged += (_, _) => states.Writer.TryWrite(host.IsConnected);
        await host.StartAsync(Ct());
        Assert.True(await states.Reader.ReadAsync(Ct()));
        var first = Assert.Single(http.Channels);
        await first.DeliverInbound(Json("""{"type":"channel-open","channelId":"old","request":{"type":"recording"}}"""));
        await opened.Task.WaitAsync(Ct());

        first.CompleteInbound();
        Assert.False(await states.Reader.ReadAsync(Ct()));
        await disposed.Task.WaitAsync(Ct());
        Assert.True(await states.Reader.ReadAsync(Ct()));
        Assert.Equal(2, http.Channels.Count);
        Assert.Equal(1, first.DisposeCount);
        Assert.False(states.Reader.TryRead(out _));
    }

    [Fact]
    public async Task DispatcherExit_RevokesHubRegistrationAndStatusBeforeReconnect()
    {
        var profile = new EntityId("11111111-1111-4111-8111-111111111111");
        var oldHub = new EntityId("22222222-2222-4222-8222-222222222222");
        var newHub = new EntityId("33333333-3333-4333-8333-333333333333");
        var store = new RecordingRouteStore();
        var statuses = new ReverseConnectionStatusRegistry();
        await using var server = new ReverseHttpServerTransportFactory(statuses);
        var http = new FakeHubHttpTransportFactory(server);
        var factory = new ReverseHttpClientTransportFactory(
            http, "https://hub.example", profile.ToString(), store, oldHub);
        var states = Channel.CreateUnbounded<(bool ClientConnected, bool HubRegistered, int StatusCount)>();
        await using var host = new WorkspacesTransportHost(
            new TransportRegistry(), [factory], Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        host.ConnectionStateChanged += (_, _) => states.Writer.TryWrite((
            host.IsConnected, server.IsRegistered(profile.ToString()), statuses.GetConnectedInstances().Count));
        await host.StartAsync(Ct());
        Assert.Equal((true, true, 1), await states.Reader.ReadAsync(Ct()));
        Assert.Equal($"upsert:reverse-http:{oldHub}", await store.Operations.ReadAsync(Ct()));

        var first = Assert.Single(http.Channels);
        store.FailNextUpsert();
        await first.DeliverInbound(Json($$"""
            {"type":"reverse-registration-info","hub-profile-entity-id":"{{newHub}}"}
            """));

        Assert.Equal((false, false, 0), await states.Reader.ReadAsync(Ct()));
        Assert.Equal((true, true, 1), await states.Reader.ReadAsync(Ct()));
        Assert.True(first.Disposed);
        Assert.Equal(2, http.Channels.Count);
        Assert.Single(statuses.GetConnectedInstances());
    }

    [Fact]
    public async Task DispatcherStopsAndReconnectFails_ReportsDisconnectedState()
    {
        var profile = new EntityId("11111111-1111-4111-8111-111111111111");
        var hubId = new EntityId("22222222-2222-4222-8222-222222222222");
        var store = new RecordingRouteStore();
        var http = new FakeHubHttpTransportFactory();
        var factory = new ReverseHttpClientTransportFactory(
            http, "https://hub.example", profile.ToString(), store, hubId);
        var states = Channel.CreateUnbounded<bool>();
        await using var host = new WorkspacesTransportHost(
            new TransportRegistry(), [factory], Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        host.ConnectionStateChanged += (_, _) => states.Writer.TryWrite(host.IsConnected);
        await host.StartAsync(Ct());
        Assert.True(await states.Reader.ReadAsync(Ct()));
        Assert.Equal($"upsert:reverse-http:{hubId}", await store.Operations.ReadAsync(Ct()));

        http.FailNextConnection = true;
        var first = Assert.Single(http.Channels);
        first.CompleteInbound();

        Assert.False(await states.Reader.ReadAsync(Ct()));
        Assert.False(await states.Reader.ReadAsync(Ct()));
        Assert.True(first.Disposed);
        Assert.False(host.IsConnected);
        Assert.Empty(factory.HubUrls);
        Assert.Equal(nameof(TransportException), host.LastRegistrationFailureType);
        Assert.Equal($"remove:reverse-http:{hubId}", await store.Operations.ReadAsync(Ct()));
        Assert.Single(http.Channels);
        Assert.False(states.Reader.TryRead(out _));
    }

    [Fact]
    public async Task HostShutdown_CancelsDispatcherWithoutReconnecting()
    {
        var http = new FakeHubHttpTransportFactory();
        var factory = new ReverseHttpClientTransportFactory(http, "https://hub.example", "machine-a");
        var states = Channel.CreateUnbounded<bool>();
        var host = new WorkspacesTransportHost(
            new TransportRegistry(), [factory], Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        host.ConnectionStateChanged += (_, _) => states.Writer.TryWrite(host.IsConnected);
        await host.StartAsync(Ct());
        Assert.True(await states.Reader.ReadAsync(Ct()));
        var first = Assert.Single(http.Channels);

        await host.DisposeAsync();

        Assert.False(host.IsConnected);
        Assert.True(first.Disposed);
        Assert.Single(http.Channels);
        Assert.False(states.Reader.TryRead(out _));
        Assert.Null(host.LastRegistrationFailureType);
    }

    [Fact]
    public async Task HostShutdown_DuringReconnect_CancelsAttemptAndKeepsRegistrationRevoked()
    {
        var http = new FakeHubHttpTransportFactory();
        var factory = new ReverseHttpClientTransportFactory(http, "https://hub.example", "machine-a");
        var reconnectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverConnect = new TaskCompletionSource<ITransport?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = Channel.CreateUnbounded<bool>();
        var host = new WorkspacesTransportHost(
            new TransportRegistry(), [factory], Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        host.ConnectionStateChanged += (_, _) => states.Writer.TryWrite(host.IsConnected);
        await host.StartAsync(Ct());
        Assert.True(await states.Reader.ReadAsync(Ct()));
        http.ConnectOverride = ct =>
        {
            reconnectStarted.TrySetResult();
            return neverConnect.Task.WaitAsync(ct);
        };
        var first = Assert.Single(http.Channels);

        first.CompleteInbound();
        Assert.False(await states.Reader.ReadAsync(Ct()));
        await reconnectStarted.Task.WaitAsync(Ct());
        await host.DisposeAsync().AsTask().WaitAsync(Ct());

        Assert.False(host.IsConnected);
        Assert.True(first.Disposed);
        Assert.Single(http.Channels);
        Assert.False(states.Reader.TryRead(out _));
    }

    [Fact]
    public async Task RegistrationInfo_UsesHubProfileIdentityForStablePublishedRoute()
    {
        var profileId = new EntityId("11111111-1111-4111-8111-111111111111");
        var hubProfileId = new EntityId("22222222-2222-4222-8222-222222222222");
        var store = new RecordingRouteStore();
        var http = new FakeHubHttpTransportFactory();
        var factory = new ReverseHttpClientTransportFactory(
            http,
            "https://hub.example/",
            profileId.ToString(),
            store,
            hubProfileEntityId: null);
        await using var host = new WorkspacesTransportHost(new TransportRegistry(), [factory], Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var ct = Ct();
        await host.StartAsync(ct);

        await http.Channels.Single().DeliverInbound(
            Json(
                $$"""
                {
                  "type": "reverse-registration-info",
                  "hub-profile-entity-id": "{{hubProfileId}}"
                }
                """));
        var republished = await store.Operations.ReadAsync(ct);

        Assert.Equal($"upsert:reverse-http:{hubProfileId}", republished);
        Assert.False(store.Operations.TryRead(out _));
    }

    private static CancellationToken Ct() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeHubHttpTransportFactory(ReverseHttpServerTransportFactory? server = null) : ITransportFactory
    {
        private readonly List<FakeRegistrationChannel> channels = [];

        public IReadOnlyList<FakeRegistrationChannel> Channels => this.channels;

        public bool FailNextConnection { get; set; }

        public Func<CancellationToken, Task<ITransport?>>? ConnectOverride { get; set; }

        public Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            if (this.ConnectOverride is not null)
            {
                return this.ConnectOverride(ct);
            }

            if (this.FailNextConnection)
            {
                this.FailNextConnection = false;
                throw new IOException("Hub unavailable.");
            }

            return Task.FromResult<ITransport?>(new FakeHubTransport(this.channels, server));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeHubTransport(
        List<FakeRegistrationChannel> channels, ReverseHttpServerTransportFactory? server) : ITransport
    {
        public async Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            var channel = new FakeRegistrationChannel(request.Clone());
            if (server is not null)
            {
                channel.SetServerLease(await server.OnChannelOpenAsync(request, new ServerSideChannel(channel), ct));
            }

            channels.Add(channel);
            return channel;
        }

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class ServerSideChannel(FakeRegistrationChannel channel) : IMessageChannel
        {
            public ChannelWriter<JsonElement> Writer => channel.inbound.Writer;

            public ChannelReader<JsonElement> Reader => channel.outbound.Reader;

            public ValueTask DisposeAsync()
            {
                channel.inbound.Writer.TryComplete();
                channel.outbound.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeRegistrationChannel(JsonElement registerRequest) : IMessageChannel
    {
        internal readonly Channel<JsonElement> inbound = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        internal readonly Channel<JsonElement> outbound = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        private IAsyncDisposable? serverLease;

        public JsonElement RegisterRequest { get; } = registerRequest;

        public int DisposeCount { get; private set; }

        public bool Disposed => this.DisposeCount > 0;

        public ChannelWriter<JsonElement> Writer => this.outbound.Writer;

        public ChannelReader<JsonElement> Reader => this.inbound.Reader;

        public void SetServerLease(IAsyncDisposable? lease) => this.serverLease = lease;

        public ValueTask DeliverInbound(JsonElement frame) => this.inbound.Writer.WriteAsync(frame);

        public void CompleteInbound(Exception? error = null) => this.inbound.Writer.TryComplete(error);

        public bool CanDeliverInbound(JsonElement frame) => this.inbound.Writer.TryWrite(frame);

        public ValueTask<JsonElement> ReadOutbound(CancellationToken ct) => this.outbound.Reader.ReadAsync(ct);

        public async ValueTask DisposeAsync()
        {
            this.DisposeCount++;
            this.inbound.Writer.TryComplete();
            this.outbound.Writer.TryComplete();
            if (this.serverLease is not null)
            {
                await this.serverLease.DisposeAsync();
            }
        }
    }

    private sealed class RecordingIdentityChannelListener(
        TransportPeerIdentityProvider identities, Action<string> onOpen, Action<string> onDispose) : ITransportListener
    {
        public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
        {
            if (!request.TryGetProperty("type", out var type) || type.GetString() != "identity")
                return Task.FromResult<IAsyncDisposable?>(null);
            var peer = identities.GetRequiredIdentity(channel).StablePeerId;
            onOpen(peer);
            return Task.FromResult<IAsyncDisposable?>(new DisposeCallback(() => onDispose(peer)));
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingChannelListener(Action onOpen, Action onDispose) : ITransportListener
    {
        public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
        {
            onOpen();
            return Task.FromResult<IAsyncDisposable?>(new DisposeCallback(onDispose));
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DisposeCallback(Action callback) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            callback();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingShellStreamListener(Action<JsonElement> onStream) : ITransportListener
    {
        public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
        {
            if (request.ValueKind != JsonValueKind.Object
                || !request.TryGetProperty("type", out var typeElement)
                || !string.Equals(typeElement.GetString(), "shell", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult<IAsyncDisposable?>(null);
            }

            onStream(request.Clone());
            return Task.FromResult<IAsyncDisposable?>(new NoopDisposable());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRouteStore : IReachabilityRouteStore
    {
        private readonly Channel<string> operations = Channel.CreateUnbounded<string>();
        private int failNextUpsert;

        public ChannelReader<string> Operations => this.operations.Reader;

        public void FailNextUpsert() => Interlocked.Exchange(ref this.failNextUpsert, 1);

        public Task<IReadOnlyList<ReachabilityRoute>> GetRoutesAsync(
            EntityId profileEntityId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReachabilityRoute>>([]);

        public Task UpsertRouteAsync(
            EntityId profileEntityId,
            ReachabilityRoute route,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref this.failNextUpsert, 0) == 1)
            {
                this.operations.Writer.TryWrite($"failed-upsert:{route.RouteId}");
                throw new IOException("Unanticipated route store failure.");
            }

            this.operations.Writer.TryWrite($"upsert:{route.RouteId}");
            return Task.CompletedTask;
        }

        public Task RemoveRouteAsync(
            EntityId profileEntityId,
            string routeId,
            EntityId ownerProfileEntityId,
            CancellationToken cancellationToken = default)
        {
            this.operations.Writer.TryWrite($"remove:{routeId}");
            return Task.CompletedTask;
        }

        public Task ClearOwnedRoutesAsync(
            EntityId profileEntityId,
            EntityId ownerProfileEntityId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class EchoChatClient : IChatClient
    {
        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = messages.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "pong");
            await Task.CompletedTask;
        }
    }
}
