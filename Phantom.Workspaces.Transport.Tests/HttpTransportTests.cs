using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Http;

namespace Phantom.Workspaces.Transport.Tests;

public class HttpTransportTests
{
    [Fact]
    public async Task HttpTransport_ChannelMessage_DeliveredToReader()
    {
        using var socket = new TestWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var channel = await transport.ConnectToMessageChannelAsync(Json("{\"target\":\"echo\"}"));
        var openFrame = await socket.ReadSentFrameAsync();
        await socket.ReceiveTextAsync(new TransportFrame
        {
            Type = TransportFrame.Types.ChannelMessage,
            ChannelId = openFrame.ChannelId,
            Payload = Json("{\"value\":\"hello\"}"),
        });

        var message = await channel.Reader.ReadAsync(TestCancellationToken());

        Assert.Equal("hello", message.GetProperty("value").GetString());
    }

    [Fact]
    public async Task HttpTransport_ChannelClose_DrainsBufferedMessages()
    {
        using var socket = new TestWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var channel = await transport.ConnectToMessageChannelAsync(Json("{}"));
        var openFrame = await socket.ReadSentFrameAsync();
        await socket.ReceiveTextAsync(new TransportFrame { Type = TransportFrame.Types.ChannelMessage, ChannelId = openFrame.ChannelId, Payload = Json("{\"index\":1}") });
        await socket.ReceiveTextAsync(new TransportFrame { Type = TransportFrame.Types.ChannelMessage, ChannelId = openFrame.ChannelId, Payload = Json("{\"index\":2}") });
        await socket.ReceiveTextAsync(new TransportFrame { Type = TransportFrame.Types.ChannelClose, ChannelId = openFrame.ChannelId });
        await socket.ReceiveTextAsync(new TransportFrame { Type = TransportFrame.Types.ChannelMessage, ChannelId = openFrame.ChannelId, Payload = Json("{\"index\":3}") });

        var first = await channel.Reader.ReadAsync(TestCancellationToken());
        var second = await channel.Reader.ReadAsync(TestCancellationToken());
        await channel.Reader.Completion.WaitAsync(TestTimeout);

        Assert.Equal(1, first.GetProperty("index").GetInt32());
        Assert.Equal(2, second.GetProperty("index").GetInt32());
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task HttpTransport_ChannelOpenError_FaultsChannelReader()
    {
        using var socket = new TestWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var channel = await transport.ConnectToMessageChannelAsync(Json("{}"));
        var openFrame = await socket.ReadSentFrameAsync();
        await socket.ReceiveTextAsync(new TransportFrame
        {
            Type = TransportFrame.Types.ChannelOpenError,
            ChannelId = openFrame.ChannelId,
            Message = "no listener",
        });

        var exception = await Assert.ThrowsAsync<TransportException>(async () =>
            await channel.Reader.Completion.WaitAsync(TestTimeout));
        Assert.Contains("no listener", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_Keepalive_SentEvery30Seconds()
    {
        using var socket = new TestWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromMilliseconds(10));

        var frame = await socket.ReadSentFrameAsync();

        Assert.Equal(TransportFrame.Types.Keepalive, frame.Type);
    }

    [Fact]
    public async Task HttpTransport_Dispose_SendsTransportCloseAndFaultsChannels()
    {
        using var socket = new TestWebSocket();
        var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var channel = await transport.ConnectToMessageChannelAsync(Json("{}"));
        var openFrame = await socket.ReadSentFrameAsync();
        await socket.ReceiveTextAsync(new TransportFrame { Type = TransportFrame.Types.ChannelMessage, ChannelId = openFrame.ChannelId, Payload = Json("{\"ready\":true}") });

        await transport.DisposeAsync();
        var closeFrame = await socket.ReadSentFrameAsync();

        Assert.Equal(TransportFrame.Types.TransportClose, closeFrame.Type);
        Assert.True((await channel.Reader.ReadAsync(TestCancellationToken())).GetProperty("ready").GetBoolean());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await channel.Reader.Completion.WaitAsync(TestTimeout));
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static CancellationToken TestCancellationToken()
    {
        var cts = new CancellationTokenSource(TestTimeout);
        return cts.Token;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class TestWebSocket : WebSocket
    {
        private readonly Channel<ReceiveMessage> received = Channel.CreateUnbounded<ReceiveMessage>();
        private readonly Channel<SentMessage> sent = Channel.CreateUnbounded<SentMessage>();
        private WebSocketState state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => this.state;

        public override string? SubProtocol => null;

        public async Task ReceiveTextAsync(TransportFrame frame)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
            await this.received.Writer.WriteAsync(new ReceiveMessage(bytes, WebSocketMessageType.Text));
        }

        public async Task<TransportFrame> ReadSentFrameAsync()
        {
            while (true)
            {
                var message = await this.sent.Reader.ReadAsync(TestCancellationToken());
                if (message.MessageType == WebSocketMessageType.Text)
                {
                    return JsonSerializer.Deserialize<TransportFrame>(message.Payload)!;
                }
            }
        }

        public override void Abort()
        {
            this.state = WebSocketState.Aborted;
            this.received.Writer.TryComplete();
            this.sent.Writer.TryComplete();
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            this.state = WebSocketState.Closed;
            this.received.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            this.state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            this.state = WebSocketState.Closed;
            this.received.Writer.TryComplete();
            this.sent.Writer.TryComplete();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var message = await this.received.Reader.ReadAsync(cancellationToken);
            message.Payload.CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(message.Payload.Length, message.MessageType, true);
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var payload = buffer.AsSpan().ToArray();
            await this.sent.Writer.WriteAsync(new SentMessage(payload, messageType), cancellationToken);
        }

        private sealed record ReceiveMessage(byte[] Payload, WebSocketMessageType MessageType);

        private sealed record SentMessage(byte[] Payload, WebSocketMessageType MessageType);
    }
}

public class ServerHttpTransportTests
{
    [Fact]
    public async Task ServerHttpTransport_ChannelOpen_RoutesToListener()
    {
        using var socket = new TestWebSocket();
        var registry = new TransportRegistry();
        var listener = new RecordingListener();
        registry.Register(listener);
        await using var transport = new ServerHttpTransport(socket, registry, TimeSpan.FromHours(1));
        var runTask = transport.RunAsync();

        await socket.ReceiveTextAsync(new TransportFrame
        {
            Type = TransportFrame.Types.ChannelOpen,
            ChannelId = "channel-1",
            Request = Json("{\"target\":\"echo\"}"),
        });
        var channel = await listener.Channel.Task.WaitAsync(TestTimeout);
        await socket.ReceiveTextAsync(new TransportFrame
        {
            Type = TransportFrame.Types.ChannelMessage,
            ChannelId = "channel-1",
            Payload = Json("{\"value\":\"hello\"}"),
        });

        var message = await channel.Reader.ReadAsync(TestCancellationToken());

        Assert.Equal("echo", listener.Request.GetProperty("target").GetString());
        Assert.Equal("hello", message.GetProperty("value").GetString());
        await transport.DisposeAsync();
        try { await runTask.WaitAsync(TestTimeout); } catch { }
    }

    [Fact]
    public async Task ServerHttpTransport_ChannelOpen_NoListener_SendsNotFound()
    {
        using var socket = new TestWebSocket();
        var registry = new TransportRegistry();
        await using var transport = new ServerHttpTransport(socket, registry, TimeSpan.FromHours(1));
        var runTask = transport.RunAsync();

        await socket.ReceiveTextAsync(new TransportFrame
        {
            Type = TransportFrame.Types.ChannelOpen,
            ChannelId = "channel-1",
            Request = Json("{}"),
        });

        var frame = await socket.ReadSentFrameAsync();

        Assert.Equal(TransportFrame.Types.ChannelOpenError, frame.Type);
        Assert.Equal("not-found", frame.ErrorCode);
        await transport.DisposeAsync();
        try { await runTask.WaitAsync(TestTimeout); } catch { }
    }

    [Fact]
    public async Task ServerHttpTransport_LeaseExpiry_SendsTransportClose()
    {
        using var socket = new TestWebSocket();
        var registry = new TransportRegistry();
        await using var transport = new ServerHttpTransport(socket, registry, TimeSpan.FromMilliseconds(20));
        var runTask = transport.RunAsync();

        var frame = await socket.ReadSentFrameAsync();

        Assert.Equal(TransportFrame.Types.TransportClose, frame.Type);
        try { await runTask.WaitAsync(TestTimeout); } catch { }
    }

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static CancellationToken TestCancellationToken()
    {
        var cts = new CancellationTokenSource(TestTimeout);
        return cts.Token;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class RecordingListener : ITransportListener
    {
        public TaskCompletionSource<IMessageChannel> Channel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JsonElement Request { get; private set; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
        {
            this.Request = request.Clone();
            this.Channel.SetResult(channel);
            return Task.FromResult<IAsyncDisposable?>(this);
        }

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);
    }

    private sealed class TestWebSocket : WebSocket
    {
        private readonly Channel<ReceiveMessage> received = Channel.CreateUnbounded<ReceiveMessage>();
        private readonly Channel<SentMessage> sent = Channel.CreateUnbounded<SentMessage>();
        private WebSocketState state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;

        public override WebSocketState State => this.state;

        public async Task ReceiveTextAsync(TransportFrame frame)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
            await this.received.Writer.WriteAsync(new ReceiveMessage(bytes, WebSocketMessageType.Text));
        }

        public async Task<TransportFrame> ReadSentFrameAsync()
        {
            while (true)
            {
                var message = await this.sent.Reader.ReadAsync(TestCancellationToken());
                if (message.MessageType == WebSocketMessageType.Text)
                {
                    return JsonSerializer.Deserialize<TransportFrame>(message.Payload)!;
                }
            }
        }

        public override void Abort()
        {
            this.state = WebSocketState.Aborted;
            this.received.Writer.TryComplete();
            this.sent.Writer.TryComplete();
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            this.state = WebSocketState.Closed;
            this.received.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            this.state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            this.state = WebSocketState.Closed;
            this.received.Writer.TryComplete();
            this.sent.Writer.TryComplete();
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var message = await this.received.Reader.ReadAsync(cancellationToken);
            message.Payload.CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(message.Payload.Length, message.MessageType, true);
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var payload = buffer.AsSpan().ToArray();
            await this.sent.Writer.WriteAsync(new SentMessage(payload, messageType), cancellationToken);
        }

        private sealed record ReceiveMessage(byte[] Payload, WebSocketMessageType MessageType);

        private sealed record SentMessage(byte[] Payload, WebSocketMessageType MessageType);
    }
}

public class HttpClientTransportFactoryTests
{
    [Fact]
    public async Task HttpClientTransportFactory_HttpDescriptor_ConnectsWebSocket()
    {
        await using var server = await WebSocketHandshakeServer.StartAsync();
        await using var factory = new HttpClientTransportFactory();

        var transport = await factory.ConnectToAsync(Json($"{{\"type\":\"http\",\"url\":\"http://127.0.0.1:{server.Port}\",\"target\":{{\"type\":\"local\"}}}}"));
        Assert.NotNull(transport);
        await using (transport)
        {
            var request = await server.Request.WaitAsync(TestTimeout);
            Assert.Equal("GET", request.Method);
            Assert.Equal("/transport/connect", request.Path);
            Assert.True(request.Headers.ContainsKey("sec-websocket-key"));
        }
    }

    [Fact]
    public async Task HttpClientTransportFactory_DevTunnelToken_SetsHeader()
    {
        await using var server = await WebSocketHandshakeServer.StartAsync();
        await using var factory = new HttpClientTransportFactory();

        var transport = await factory.ConnectToAsync(Json($"{{\"type\":\"http\",\"url\":\"http://127.0.0.1:{server.Port}\",\"dev-tunnel-token\":\"abc123\",\"target\":{{\"type\":\"local\"}}}}"));
        Assert.NotNull(transport);
        await using (transport)
        {
            var request = await server.Request.WaitAsync(TestTimeout);
            Assert.True(request.Headers.TryGetValue("x-tunnel-authorization", out var authorization));
            Assert.Equal("tunnel abc123", authorization);
        }
    }

    [Fact]
    public async Task HttpClientTransportFactory_NonHttpDescriptor_ReturnsNull()
    {
        await using var factory = new HttpClientTransportFactory();

        var transport = await factory.ConnectToAsync(Json("{\"type\":\"local\"}"));

        Assert.Null(transport);
    }

    [Fact]
    public async Task HttpClientTransportFactory_ConnectTo_InProcessServer_ReturnsWorkingTransport()
    {
        await using var host = await InProcessHttpTransportHost.StartAsync(new EchoChannelListener());

        var factory = new HttpClientTransportFactory();
        using var descriptor = JsonDocument.Parse($$"""{"type":"http","url":"{{host.BaseUrl}}"}""");
        var transport = await factory.ConnectToAsync(descriptor.RootElement).WaitAsync(InProcessTimeout);

        Assert.NotNull(transport);
        await using (transport)
        {
            using var request = JsonDocument.Parse("""{"target":"echo"}""");
            var channel = await transport!.ConnectToMessageChannelAsync(request.RootElement).WaitAsync(InProcessTimeout);
            using var payload = JsonDocument.Parse("""{"value":"hello"}""");
            await channel.Writer.WriteAsync(payload.RootElement).AsTask().WaitAsync(InProcessTimeout);

            var reply = await channel.Reader.ReadAsync().AsTask().WaitAsync(InProcessTimeout);
            Assert.Equal("hello", reply.GetProperty("value").GetString());

            await channel.DisposeAsync();
        }
    }

    [Fact]
    public async Task HttpClientTransportFactory_ConnectTo_ConcurrentConnects_AllSucceed()
    {
        await using var host = await InProcessHttpTransportHost.StartAsync(new EchoChannelListener());

        var factory = new HttpClientTransportFactory();
        using var descriptor = JsonDocument.Parse($$"""{"type":"http","url":"{{host.BaseUrl}}"}""");
        var descriptorElement = descriptor.RootElement;

        var connectTasks = Enumerable.Range(0, 8)
            .Select(_ => factory.ConnectToAsync(descriptorElement))
            .ToArray();

        var transports = await Task.WhenAll(connectTasks).WaitAsync(InProcessTimeout);

        Assert.All(transports, t => Assert.NotNull(t));

        try
        {
            var channelTasks = transports.Select(async (t, i) =>
            {
                using var req = JsonDocument.Parse("""{"target":"echo"}""");
                var ch = await t!.ConnectToMessageChannelAsync(req.RootElement).WaitAsync(InProcessTimeout);
                using var payload = JsonDocument.Parse($$"""{"value":"concurrent-{{i}}"}""");
                await ch.Writer.WriteAsync(payload.RootElement).AsTask().WaitAsync(InProcessTimeout);
                var reply = await ch.Reader.ReadAsync().AsTask().WaitAsync(InProcessTimeout);
                Assert.Equal($"concurrent-{i}", reply.GetProperty("value").GetString());
                await ch.DisposeAsync();
            }).ToArray();

            await Task.WhenAll(channelTasks).WaitAsync(InProcessTimeout);
        }
        finally
        {
            foreach (var t in transports)
            {
                if (t is not null)
                {
                    await t.DisposeAsync();
                }
            }
        }
    }

    private static readonly TimeSpan InProcessTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class WebSocketHandshakeServer : IAsyncDisposable
    {
        private static readonly byte[] WebSocketGuidBytes = Encoding.ASCII.GetBytes("258EAFA5-E914-47DA-95CA-C5AB0DC85B11");
        private readonly TcpListener listener;
        private readonly CancellationTokenSource shutdown = new();
        private readonly TaskCompletionSource<HandshakeRequest> request = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task acceptTask;
        private TcpClient? client;

        private WebSocketHandshakeServer(TcpListener listener)
        {
            this.listener = listener;
            this.Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            this.acceptTask = Task.Run(this.AcceptAsync);
        }

        public int Port { get; }

        public Task<HandshakeRequest> Request => this.request.Task;

        public static Task<WebSocketHandshakeServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new WebSocketHandshakeServer(listener));
        }

        public async ValueTask DisposeAsync()
        {
            this.shutdown.Cancel();
            this.listener.Stop();
            this.client?.Dispose();

            try
            {
                await this.acceptTask.WaitAsync(TestTimeout);
            }
            catch
            {
            }

            this.shutdown.Dispose();
        }

        private async Task AcceptAsync()
        {
            try
            {
                this.client = await this.listener.AcceptTcpClientAsync(this.shutdown.Token);
                await using var stream = this.client.GetStream();
                var request = await ReadRequestAsync(stream, this.shutdown.Token);
                this.request.TrySetResult(request);
                var accept = CreateAcceptValue(request.Headers["sec-websocket-key"]);
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    $"Sec-WebSocket-Accept: {accept}\r\n" +
                    "\r\n");
                await stream.WriteAsync(response, this.shutdown.Token);
                await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(this.shutdown.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                this.request.TrySetException(ex);
            }
        }

        private static async Task<HandshakeRequest> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[4096];
            var used = 0;
            while (used < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(used), ct);
                if (read == 0)
                {
                    break;
                }

                used += read;
                if (Encoding.ASCII.GetString(buffer, 0, used).Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    break;
                }
            }

            var text = Encoding.ASCII.GetString(buffer, 0, used);
            var lines = text.Split("\r\n", StringSplitOptions.None);
            var requestLine = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                if (string.IsNullOrEmpty(line))
                {
                    break;
                }

                var separator = line.IndexOf(':', StringComparison.Ordinal);
                if (separator > 0)
                {
                    headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }
            }

            return new HandshakeRequest(requestLine[0], requestLine[1], headers);
        }

        private static string CreateAcceptValue(string key)
        {
            var keyBytes = Encoding.ASCII.GetBytes(key);
            var input = new byte[keyBytes.Length + WebSocketGuidBytes.Length];
            keyBytes.CopyTo(input, 0);
            WebSocketGuidBytes.CopyTo(input, keyBytes.Length);
            return Convert.ToBase64String(SHA1.HashData(input));
        }
    }

    private sealed record HandshakeRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers);

    private sealed class EchoChannelListener : ITransportListener
    {
        public Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(new EchoLease(channel));

        public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
            => Task.FromResult<IAsyncDisposable?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class EchoLease : IAsyncDisposable
        {
            private readonly IMessageChannel channel;
            private readonly CancellationTokenSource cts = new();
            private readonly Task pump;

            public EchoLease(IMessageChannel channel)
            {
                this.channel = channel;
                this.pump = Task.Run(this.PumpAsync);
            }

            private async Task PumpAsync()
            {
                try
                {
                    while (await this.channel.Reader.WaitToReadAsync(this.cts.Token).ConfigureAwait(false))
                    {
                        while (this.channel.Reader.TryRead(out var msg))
                        {
                            await this.channel.Writer.WriteAsync(msg.Clone(), this.cts.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch
                {
                }
            }

            public async ValueTask DisposeAsync()
            {
                await this.cts.CancelAsync().ConfigureAwait(false);
                try
                {
                    await this.pump.ConfigureAwait(false);
                }
                catch
                {
                }

                this.cts.Dispose();
            }
        }
    }

    private sealed class InProcessHttpTransportHost : IAsyncDisposable
    {
        private readonly Microsoft.AspNetCore.Builder.WebApplication app;
        private readonly HttpServerTransportFactory serverFactory;

        private InProcessHttpTransportHost(Microsoft.AspNetCore.Builder.WebApplication app, HttpServerTransportFactory serverFactory, string baseUrl)
        {
            this.app = app;
            this.serverFactory = serverFactory;
            this.BaseUrl = baseUrl;
        }

        public string BaseUrl { get; }

        public static async Task<InProcessHttpTransportHost> StartAsync(ITransportListener listener)
        {
            var registry = new TransportRegistry();
            registry.Register(listener);
            var serverFactory = new HttpServerTransportFactory(registry);

            var port = GetFreePort();
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, port));
            var app = builder.Build();
            app.UseWebSockets();
            serverFactory.Map(app);
            await app.StartAsync().ConfigureAwait(false);
            return new InProcessHttpTransportHost(app, serverFactory, $"http://127.0.0.1:{port}");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await this.serverFactory.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                await this.app.StopAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            await this.app.DisposeAsync().ConfigureAwait(false);
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}

