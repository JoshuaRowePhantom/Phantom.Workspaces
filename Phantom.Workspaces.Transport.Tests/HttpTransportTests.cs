using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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

        var connectTask = transport.ConnectToMessageChannelAsync(Json("{\"target\":\"echo\"}"));
        var openFrame = await socket.ReadSentFrameAsync();
        await socket.ReceiveTextAsync(new TransportFrame
        {
            Type = TransportFrame.Types.ChannelMessage,
            ChannelId = openFrame.ChannelId,
            Payload = Json("{\"value\":\"hello\"}"),
        });

        var channel = await connectTask.WaitAsync(TestTimeout);
        var message = await channel.Reader.ReadAsync(TestCancellationToken());

        Assert.Equal("hello", message.GetProperty("value").GetString());
    }

    [Fact]
    public async Task HttpTransport_ChannelClose_DrainsBufferedMessages()
    {
        using var socket = new TestWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var connectTask = transport.ConnectToMessageChannelAsync(Json("{}"));
        var openFrame = await socket.ReadSentFrameAsync();
        await socket.ReceiveTextAsync(new TransportFrame { Type = TransportFrame.Types.ChannelMessage, ChannelId = openFrame.ChannelId, Payload = Json("{\"index\":1}") });
        var channel = await connectTask.WaitAsync(TestTimeout);
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
    public async Task HttpTransport_ChannelOpenError_FaultsConnect()
    {
        using var socket = new TestWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var connectTask = transport.ConnectToMessageChannelAsync(Json("{}"));
        var openFrame = await socket.ReadSentFrameAsync();
        await socket.ReceiveTextAsync(new TransportFrame
        {
            Type = TransportFrame.Types.ChannelOpenError,
            ChannelId = openFrame.ChannelId,
            Message = "no listener",
        });

        var exception = await Assert.ThrowsAsync<TransportException>(async () => await connectTask.WaitAsync(TestTimeout));
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

        var connectTask = transport.ConnectToMessageChannelAsync(Json("{}"));
        var openFrame = await socket.ReadSentFrameAsync();
        await socket.ReceiveTextAsync(new TransportFrame { Type = TransportFrame.Types.ChannelMessage, ChannelId = openFrame.ChannelId, Payload = Json("{\"ready\":true}") });
        var channel = await connectTask.WaitAsync(TestTimeout);

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
}
