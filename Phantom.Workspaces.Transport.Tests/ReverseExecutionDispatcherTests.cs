using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Transport.Chat;
using Phantom.Workspaces.Transport.Mcp;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class ReverseExecutionDispatcherTests
{
    [Fact]
    public async Task ExecutorDispatcher_RelayedChannelOpen_DispatchesToChatListener()
    {
        await using var underlying = new UnderlyingChannel();
        var registry = new TransportRegistry();
        registry.Register(new ChatClientTransportListener(new EchoChatClient()));
        await using var dispatcher = new ReverseExecutionDispatcher(underlying, registry);

        await underlying.DeliverInbound(Json("""{"type":"channel-open","channelId":"ch1","request":{"type":"chat-client"}}"""));
        await underlying.DeliverInbound(Json("""{"type":"channel-message","channelId":"ch1","payload":{"type":"process-streaming","content":{"role":"user","text":"hello"}}}"""));

        var update = await underlying.Outbound.ReadAsync(Ct());

        Assert.Equal("channel-message", update.GetProperty("type").GetString());
        Assert.Equal("ch1", update.GetProperty("channelId").GetString());
        Assert.Equal("streaming-update", update.GetProperty("payload").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ExecutorDispatcher_RelayedChannelOpen_DispatchesToMcpAndShellListeners()
    {
        await using var underlying = new UnderlyingChannel();
        var mcpRequests = new List<JsonElement>();
        var streamRequests = new List<JsonElement>();
        var registry = new TransportRegistry();
        registry.Register(new McpTransportListener((request, channel, ct) =>
        {
            mcpRequests.Add(request.Clone());
            return Task.FromResult<IAsyncDisposable?>(new NoopDisposable());
        }));

        // The shell path is stream-based (ShellTransportListener.OnStreamOpenAsync). A recording
        // stream listener stands in here so the test stays hermetic (no real process launch); it
        // asserts the dispatcher routes relayed stream-open frames to a stream listener. The real
        // ShellTransportListener process handling is covered by ShellTransportListenerTests.
        var streamCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Register(new RecordingStreamListener(request =>
        {
            streamRequests.Add(request.Clone());
            streamCompleted.TrySetResult();
        }));

        await using var dispatcher = new ReverseExecutionDispatcher(underlying, registry);

        await underlying.DeliverInbound(Json("""{"type":"channel-open","channelId":"mcp1","request":{"type":"mcp","connection":{"transport":"stdio"}}}"""));
        await underlying.DeliverInbound(Json("""{"type":"stream-open","streamId":"sh1","request":{"type":"shell","command":"echo"}}"""));

        await streamCompleted.Task.WaitAsync(Ct());

        Assert.Equal("mcp", Assert.Single(mcpRequests).GetProperty("type").GetString());
        Assert.Equal("shell", Assert.Single(streamRequests).GetProperty("type").GetString());
    }

    [Fact]
    public async Task ExecutorDispatcher_ChannelOpenWithNoListener_SendsChannelOpenError()
    {
        await using var underlying = new UnderlyingChannel();
        var registry = new TransportRegistry();
        await using var dispatcher = new ReverseExecutionDispatcher(underlying, registry);

        await underlying.DeliverInbound(Json("""{"type":"channel-open","channelId":"orphan","request":{"type":"unknown"}}"""));

        var error = await underlying.Outbound.ReadAsync(Ct());

        Assert.Equal("channel-open-error", error.GetProperty("type").GetString());
        Assert.Equal("orphan", error.GetProperty("channelId").GetString());
        Assert.Equal("no-listener", error.GetProperty("error-code").GetString());
    }

    [Fact]
    public async Task ExecutorDispatcher_ChannelClose_DisposesSession()
    {
        await using var underlying = new UnderlyingChannel();
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new TransportRegistry();
        registry.Register(new RecordingChannelListener(() => disposed.TrySetResult()));
        await using var dispatcher = new ReverseExecutionDispatcher(underlying, registry);

        await underlying.DeliverInbound(Json("""{"type":"channel-open","channelId":"ch1","request":{"type":"recording"}}"""));
        await underlying.DeliverInbound(Json("""{"type":"channel-close","channelId":"ch1"}"""));

        await disposed.Task.WaitAsync(Ct());
    }

    [Fact]
    public async Task ExecutorDispatcher_AuthenticatedPeerClaims_AreAttachedToDispatchedChannel()
    {
        await using var underlying = new UnderlyingChannel();
        var identities = new TransportPeerIdentityProvider();
        var received = new TaskCompletionSource<TransportPeerIdentity>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new TransportRegistry();
        registry.Register(new IdentityListener(identities, identity => received.TrySetResult(identity)));
        await using var dispatcher = new ReverseExecutionDispatcher(underlying, registry, identities);

        await underlying.DeliverInbound(Json("""
            {"type":"channel-open","channelId":"identity","authenticatedPeer":{
              "authenticationScheme":"dev-tunnel","stablePeerId":"peer",
              "userEntityId":"11111111-1111-1111-1111-111111111111",
              "userComputerProfileEntityId":"22222222-2222-2222-2222-222222222222"},
             "request":{"type":"identity"}}
            """));

        var identity = await received.Task.WaitAsync(Ct());
        Assert.Equal("dev-tunnel", identity.AuthenticationScheme);
        Assert.Equal("11111111-1111-1111-1111-111111111111", identity.UserEntityId);
        Assert.Equal("22222222-2222-2222-2222-222222222222", identity.UserComputerProfileEntityId);
    }

    [Fact]
    public async Task ExecutorDispatcher_RelayedStream_RoundTripsDataFrames()
    {
        await using var underlying = new UnderlyingChannel();
        var streamReady = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new TransportRegistry();
        registry.Register(new CapturingStreamListener(stream => streamReady.TrySetResult(stream)));
        await using var dispatcher = new ReverseExecutionDispatcher(underlying, registry);

        await underlying.DeliverInbound(Json("""{"type":"stream-open","streamId":"sh1","request":{"type":"shell","command":"echo"}}"""));
        var listenerStream = await streamReady.Task.WaitAsync(Ct());

        // Inbound stream-data is demuxed by streamId and surfaced to the listener's stream.
        var inbound = new byte[] { 5, 6, 7 };
        await underlying.DeliverInbound(StreamData("sh1", inbound));
        var buffer = new byte[16];
        var read = await listenerStream.ReadAsync(buffer, Ct());
        Assert.Equal(inbound, buffer[..read]);

        // The listener's outbound write is multiplexed back as a stream-data frame.
        var outbound = new byte[] { 1, 2 };
        await listenerStream.WriteAsync(outbound, Ct());
        var data = await underlying.Outbound.ReadAsync(Ct());
        Assert.Equal("stream-data", data.GetProperty("type").GetString());
        Assert.Equal("sh1", data.GetProperty("streamId").GetString());
        Assert.Equal(outbound, Convert.FromBase64String(data.GetProperty("data").GetString()!));
    }

    [Fact]
    public async Task ExecutorDispatcher_RegistrationInfoHandlerThrowsInvalidOperation_ReportsFailureAndCleansSessions()
    {
        await using var underlying = new UnderlyingChannel();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var logs = new CapturingLoggerFactory();
        var registry = new TransportRegistry();
        registry.Register(new RecordingChannelListener(() => disposed.TrySetResult(), () => opened.TrySetResult()));
        await using var dispatcher = new ReverseExecutionDispatcher(
            underlying, registry, registrationInfoHandler: (_, _) => throw new InvalidOperationException("private details"),
            loggerFactory: logs);

        await underlying.DeliverInbound(Json("""{"type":"channel-open","channelId":"old","request":{"type":"recording"}}"""));
        await opened.Task.WaitAsync(Ct());
        await underlying.DeliverInbound(Json("""{"type":"reverse-registration-info","hub-profile-entity-id":"hub"}"""));

        var result = await dispatcher.Completion.WaitAsync(Ct());
        await disposed.Task.WaitAsync(Ct());

        Assert.Equal(ReverseDispatchStopReason.DispatchFailed, result.Reason);
        Assert.Equal(nameof(InvalidOperationException), result.ExceptionType);
        Assert.False(underlying.Reader.Completion.IsCompleted);
        Assert.Contains(logs.Entries, entry =>
            entry.Message.Contains("DispatchFailed", StringComparison.Ordinal)
            && entry.Message.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, entry => entry.Exception is not null
            || entry.Message.Contains("private details", StringComparison.Ordinal)
            || entry.Message.Contains("hub-profile-entity-id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecutorDispatcher_RegistrationChannelCompletes_ReportsChannelClosure()
    {
        await using var underlying = new UnderlyingChannel();
        await using var dispatcher = new ReverseExecutionDispatcher(underlying, new TransportRegistry());

        underlying.CompleteInbound();

        var result = await dispatcher.Completion.WaitAsync(Ct());
        Assert.Equal(ReverseDispatchStopReason.ChannelClosed, result.Reason);
        Assert.Null(result.ExceptionType);
    }

    [Fact]
    public async Task ExecutorDispatcher_RegistrationChannelFaults_ReportsChannelClosure()
    {
        await using var underlying = new UnderlyingChannel();
        await using var dispatcher = new ReverseExecutionDispatcher(underlying, new TransportRegistry());

        underlying.CompleteInbound(new IOException("private detail"));

        var result = await dispatcher.Completion.WaitAsync(Ct());
        Assert.Equal(ReverseDispatchStopReason.ChannelClosed, result.Reason);
        Assert.Equal(nameof(IOException), result.ExceptionType);
    }

    [Fact]
    public async Task ExecutorDispatcher_ReaderThrowsWhileOpen_ReportsDispatchFailure()
    {
        await using var underlying = new ReaderFailureChannel();
        await using var dispatcher = new ReverseExecutionDispatcher(underlying, new TransportRegistry());

        var result = await dispatcher.Completion.WaitAsync(Ct());

        Assert.Equal(ReverseDispatchStopReason.DispatchFailed, result.Reason);
        Assert.Equal(nameof(IOException), result.ExceptionType);
        Assert.False(underlying.Reader.Completion.IsCompleted);
    }

    [Fact]
    public async Task ExecutorDispatcher_HostDisposes_ReportsIntentionalStop()
    {
        await using var underlying = new UnderlyingChannel();
        await using var dispatcher = new ReverseExecutionDispatcher(underlying, new TransportRegistry());

        await dispatcher.DisposeAsync();

        var result = await dispatcher.Completion.WaitAsync(Ct());
        Assert.Equal(ReverseDispatchStopReason.Stopped, result.Reason);
        Assert.Null(result.ExceptionType);
        Assert.False(underlying.Reader.Completion.IsCompleted);
    }

    [Fact]
    public async Task ExecutorDispatcher_HostDisposesDuringHandler_ReportsIntentionalStop()
    {
        await using var underlying = new UnderlyingChannel();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var dispatcher = new ReverseExecutionDispatcher(
            underlying, new TransportRegistry(),
            registrationInfoHandler: async (_, ct) =>
            {
                started.TrySetResult();
                await continueHandler.Task.WaitAsync(ct);
            });

        await underlying.DeliverInbound(Json("""{"type":"reverse-registration-info","hub-profile-entity-id":"hub"}"""));
        await started.Task.WaitAsync(Ct());
        await dispatcher.DisposeAsync().AsTask().WaitAsync(Ct());

        Assert.Equal(ReverseDispatchStopReason.Stopped, (await dispatcher.Completion).Reason);
        Assert.False(underlying.Reader.Completion.IsCompleted);
    }

    [Fact]
    public async Task ExecutorDispatcher_UnexpectedCancellationWithOpenChannel_ReportsFailure()
    {
        await using var underlying = new UnderlyingChannel();
        await using var dispatcher = new ReverseExecutionDispatcher(
            underlying, new TransportRegistry(), registrationInfoHandler: (_, _) => throw new OperationCanceledException());

        await underlying.DeliverInbound(Json("""{"type":"reverse-registration-info","hub-profile-entity-id":"hub"}"""));

        var result = await dispatcher.Completion.WaitAsync(Ct());
        Assert.Equal(ReverseDispatchStopReason.DispatchFailed, result.Reason);
        Assert.Equal(nameof(OperationCanceledException), result.ExceptionType);
    }

    [Fact]
    public async Task ExecutorDispatcher_HandlerChannelClosedWithOpenReader_ReportsFailure()
    {
        await using var underlying = new UnderlyingChannel();
        await using var dispatcher = new ReverseExecutionDispatcher(
            underlying, new TransportRegistry(), registrationInfoHandler: (_, _) => throw new ChannelClosedException());

        await underlying.DeliverInbound(Json("""{"type":"reverse-registration-info","hub-profile-entity-id":"hub"}"""));

        var result = await dispatcher.Completion.WaitAsync(Ct());
        Assert.Equal(ReverseDispatchStopReason.DispatchFailed, result.Reason);
        Assert.Equal(nameof(ChannelClosedException), result.ExceptionType);
        Assert.False(underlying.Reader.Completion.IsCompleted);
    }

    private static CancellationToken Ct() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static JsonElement StreamData(string streamId, byte[] data)
        => JsonSerializer.SerializeToElement(new
        {
            type = "stream-data",
            streamId,
            data = Convert.ToBase64String(data),
        });

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class UnderlyingChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> outbound = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> inbound = Channel.CreateUnbounded<JsonElement>();

        public ChannelWriter<JsonElement> Writer => this.outbound.Writer;

        public ChannelReader<JsonElement> Reader => this.inbound.Reader;

        public ChannelReader<JsonElement> Outbound => this.outbound.Reader;

        public ValueTask DeliverInbound(JsonElement frame) => this.inbound.Writer.WriteAsync(frame);

        public void CompleteInbound(Exception? error = null) => this.inbound.Writer.TryComplete(error);

        public ValueTask DisposeAsync()
        {
            this.inbound.Writer.TryComplete();
            this.outbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReaderFailureChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> outgoing = Channel.CreateUnbounded<JsonElement>();

        public ChannelWriter<JsonElement> Writer => this.outgoing.Writer;

        public ChannelReader<JsonElement> Reader { get; } = new FailingReader();

        public ValueTask DisposeAsync()
        {
            this.outgoing.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private sealed class FailingReader : ChannelReader<JsonElement>
        {
            private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public override Task Completion => this.completion.Task;

            public override bool TryRead(out JsonElement item)
            {
                item = default;
                return false;
            }

            public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
                => ValueTask.FromException<bool>(new IOException("private detail"));
        }
    }

    private sealed class RecordingStreamListener(Action<JsonElement> onStream) : ITransportListener
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

            onStream(request);
            return Task.FromResult<IAsyncDisposable?>(new NoopDisposable());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdentityListener(
        TransportPeerIdentityProvider provider,
        Action<TransportPeerIdentity> onIdentity) : ITransportListener
    {
        public Task<IAsyncDisposable?> OnChannelOpenAsync(
            JsonElement request, IMessageChannel channel, CancellationToken ct = default)
        {
            if (request.TryGetProperty("type", out var type) && type.GetString() == "identity")
            {
                onIdentity(provider.GetRequiredIdentity(channel));
                return Task.FromResult<IAsyncDisposable?>(new NoopDisposable());
            }
            return Task.FromResult<IAsyncDisposable?>(null);
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(
            JsonElement request, Stream stream, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingChannelListener(Action onDispose, Action? onOpen = null) : ITransportListener
    {
        public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
        {
            onOpen?.Invoke();
            return Task.FromResult<IAsyncDisposable?>(new DisposeCallback(onDispose));
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class DisposeCallback(Action onDispose) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                onDispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class CapturingStreamListener(Action<Stream> onStream) : ITransportListener
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

            onStream(stream);
            return Task.FromResult<IAsyncDisposable?>(new NoopDisposable());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
