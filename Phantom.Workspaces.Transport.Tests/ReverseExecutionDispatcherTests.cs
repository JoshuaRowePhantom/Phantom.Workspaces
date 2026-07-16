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
        Assert.Equal("no-listener", error.GetProperty("errorCode").GetString());
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

    private static CancellationToken Ct() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class UnderlyingChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> outbound = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> inbound = Channel.CreateUnbounded<JsonElement>();

        public ChannelWriter<JsonElement> Writer => this.outbound.Writer;

        public ChannelReader<JsonElement> Reader => this.inbound.Reader;

        public ChannelReader<JsonElement> Outbound => this.outbound.Reader;

        public ValueTask DeliverInbound(JsonElement frame) => this.inbound.Writer.WriteAsync(frame);

        public ValueTask DisposeAsync()
        {
            this.inbound.Writer.TryComplete();
            this.outbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
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

    private sealed class RecordingChannelListener(Action onDispose) : ITransportListener
    {
        public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(new DisposeCallback(onDispose));

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
