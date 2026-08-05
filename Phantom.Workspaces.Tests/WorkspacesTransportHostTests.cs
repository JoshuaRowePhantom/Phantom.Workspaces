using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
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

        await using var host = new WorkspacesTransportHost(registry, [factoryA, factoryB]);
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

        await using var host = new WorkspacesTransportHost(registry, [factory]);
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

        await using var host = new WorkspacesTransportHost(registry, [factory]);
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

        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stateChanges = 0;

        await using var host = new WorkspacesTransportHost(registry, [factory]);
        host.ConnectionStateChanged += (_, _) =>
        {
            if (Interlocked.Increment(ref stateChanges) >= 2)
            {
                reconnected.TrySetResult();
            }
        };

        await host.StartAsync(Ct());

        var firstChannel = http.Channels.Single();

        // Simulate loss of the registration channel; the host should reconnect via ReconnectAsync,
        // which opens a fresh registration channel through the (fake) HTTP transport factory.
        firstChannel.CompleteInbound();

        await reconnected.Task.WaitAsync(Ct());

        Assert.Equal(2, http.Channels.Count);
        Assert.NotSame(firstChannel, http.Channels[^1]);
    }

    private static CancellationToken Ct() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FakeHubHttpTransportFactory : ITransportFactory
    {
        private readonly List<FakeRegistrationChannel> channels = [];

        public IReadOnlyList<FakeRegistrationChannel> Channels => this.channels;

        public Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
            => Task.FromResult<ITransport?>(new FakeHubTransport(this.channels));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeHubTransport(List<FakeRegistrationChannel> channels) : ITransport
    {
        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            var channel = new FakeRegistrationChannel(request.Clone());
            channels.Add(channel);
            return Task.FromResult<IMessageChannel>(channel);
        }

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRegistrationChannel(JsonElement registerRequest) : IMessageChannel
    {
        private readonly Channel<JsonElement> inbound = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> outbound = System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();

        public JsonElement RegisterRequest { get; } = registerRequest;

        public ChannelWriter<JsonElement> Writer => this.outbound.Writer;

        public ChannelReader<JsonElement> Reader => this.inbound.Reader;

        public ValueTask DeliverInbound(JsonElement frame) => this.inbound.Writer.WriteAsync(frame);

        public void CompleteInbound() => this.inbound.Writer.TryComplete();

        public ValueTask<JsonElement> ReadOutbound(CancellationToken ct) => this.outbound.Reader.ReadAsync(ct);

        public ValueTask DisposeAsync()
        {
            this.inbound.Writer.TryComplete();
            this.outbound.Writer.TryComplete();
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
