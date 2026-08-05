using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Http;

namespace Phantom.Workspaces.Transport.Tests;

public class HttpTransportStreamTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task HttpTransportStream_CanRead_IsTrue()
    {
        using var socket = new CapturingWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var stream = await transport.ConnectToStreamAsync(Json("{\"type\":\"echo\"}"));

        Assert.True(stream.CanRead);
        Assert.True(stream.CanWrite);
    }

    [Fact]
    public async Task HttpTransportStream_Read_AfterServerWrites_ReturnsBytes()
    {
        using var socket = new CapturingWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var stream = await transport.ConnectToStreamAsync(Json("{}"));
        var openFrame = await socket.ReadSentFrameAsync();
        var payload = Encoding.UTF8.GetBytes("hello-from-server");
        await socket.ReceiveTextAsync(new TransportFrame
        {
            Type = TransportFrame.Types.StreamData,
            StreamId = openFrame.StreamId,
            Data = Convert.ToBase64String(payload),
        });

        var buffer = new byte[64];
        var read = await stream.ReadAsync(buffer.AsMemory()).AsTask().WaitAsync(TestTimeout);

        Assert.Equal(payload.Length, read);
        Assert.Equal("hello-from-server", Encoding.UTF8.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task HttpTransportStream_Read_AfterServerClose_ReturnsZero()
    {
        using var socket = new CapturingWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var stream = await transport.ConnectToStreamAsync(Json("{}"));
        var openFrame = await socket.ReadSentFrameAsync();
        await socket.ReceiveTextAsync(new TransportFrame
        {
            Type = TransportFrame.Types.StreamClose,
            StreamId = openFrame.StreamId,
        });

        var buffer = new byte[16];
        var read = await stream.ReadAsync(buffer.AsMemory()).AsTask().WaitAsync(TestTimeout);

        Assert.Equal(0, read);
    }

    [Fact]
    public async Task HttpTransportStream_Write_DeliversStreamDataFrame()
    {
        using var socket = new CapturingWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var stream = await transport.ConnectToStreamAsync(Json("{}"));
        var openFrame = await socket.ReadSentFrameAsync();
        var payload = Encoding.UTF8.GetBytes("client-writes");
        await stream.WriteAsync(payload.AsMemory()).AsTask().WaitAsync(TestTimeout);
        var dataFrame = await socket.ReadSentFrameAsync();

        Assert.Equal(TransportFrame.Types.StreamOpen, openFrame.Type);
        Assert.Equal(TransportFrame.Types.StreamData, dataFrame.Type);
        Assert.Equal(openFrame.StreamId, dataFrame.StreamId);
        Assert.Equal(payload, Convert.FromBase64String(dataFrame.Data!));
    }

    [Fact]
    public async Task HttpTransportStream_Dispose_SendsStreamClose()
    {
        using var socket = new CapturingWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var stream = await transport.ConnectToStreamAsync(Json("{}"));
        var openFrame = await socket.ReadSentFrameAsync();
        await stream.DisposeAsync();
        var closeFrame = await socket.ReadSentFrameAsync();

        Assert.Equal(TransportFrame.Types.StreamClose, closeFrame.Type);
        Assert.Equal(openFrame.StreamId, closeFrame.StreamId);
    }

    [Fact]
    public async Task HttpTransport_TransportClose_FaultsAllOpenStreams()
    {
        using var socket = new CapturingWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var stream = await transport.ConnectToStreamAsync(Json("{}"));
        _ = await socket.ReadSentFrameAsync();
        await socket.ReceiveTextAsync(new TransportFrame { Type = TransportFrame.Types.TransportClose });

        var buffer = new byte[16];
        await Assert.ThrowsAsync<TransportException>(async () =>
            await stream.ReadAsync(buffer.AsMemory()).AsTask().WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task HttpTransport_ConnectToStreamAsync_OpenCompletesWithoutDeadlock()
    {
        using var socket = new CapturingWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var open = transport.ConnectToStreamAsync(Json("{}"));

        var stream = await open.WaitAsync(TestTimeout);

        Assert.NotNull(stream);
    }

    [Fact]
    public async Task HttpTransport_ConnectToStreamAsync_ConcurrentOpensAllComplete()
    {
        using var socket = new CapturingWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var opens = Enumerable.Range(0, 5)
            .Select(_ => transport.ConnectToStreamAsync(Json("{}")))
            .ToArray();

        var streams = await Task.WhenAll(opens).WaitAsync(TestTimeout);

        Assert.All(streams, s => Assert.NotNull(s));
        Assert.Equal(5, streams.Distinct().Count());
    }

    [Fact]
    public async Task HttpTransport_StreamData_ForUnknownStream_IsDroppedSilently()
    {
        using var socket = new CapturingWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var stream = await transport.ConnectToStreamAsync(Json("{}"));
        var openFrame = await socket.ReadSentFrameAsync();
        await socket.ReceiveTextAsync(new TransportFrame
        {
            Type = TransportFrame.Types.StreamData,
            StreamId = "unknown-stream-id",
            Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("stale")),
        });
        var payload = Encoding.UTF8.GetBytes("live");
        await socket.ReceiveTextAsync(new TransportFrame
        {
            Type = TransportFrame.Types.StreamData,
            StreamId = openFrame.StreamId,
            Data = Convert.ToBase64String(payload),
        });

        var buffer = new byte[16];
        var read = await stream.ReadAsync(buffer.AsMemory()).AsTask().WaitAsync(TestTimeout);

        Assert.Equal("live", Encoding.UTF8.GetString(buffer, 0, read));
    }

    [Fact]
    public async Task HttpTransport_StreamOpenError_FaultsStream()
    {
        using var socket = new CapturingWebSocket();
        await using var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var stream = await transport.ConnectToStreamAsync(Json("{}"));
        var openFrame = await socket.ReadSentFrameAsync();
        await socket.ReceiveTextAsync(new TransportFrame
        {
            Type = TransportFrame.Types.ChannelOpenError,
            StreamId = openFrame.StreamId,
            Message = "no stream listener",
        });

        var buffer = new byte[16];
        var ex = await Assert.ThrowsAsync<TransportException>(async () =>
            await stream.ReadAsync(buffer.AsMemory()).AsTask().WaitAsync(TestTimeout));
        Assert.Contains("no stream listener", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_OpenAndImmediateDispose_DoesNotHang()
    {
        using var socket = new CapturingWebSocket();
        var transport = new HttpTransport(socket, TimeSpan.FromHours(1));

        var stream = await transport.ConnectToStreamAsync(Json("{}"));
        await stream.DisposeAsync();
        await transport.DisposeAsync().AsTask().WaitAsync(TestTimeout);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class CapturingWebSocket : WebSocket
    {
        private readonly Channel<Received> received = Channel.CreateUnbounded<Received>();
        private readonly Channel<Sent> sent = Channel.CreateUnbounded<Sent>();
        private WebSocketState state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override WebSocketState State => this.state;

        public override string? SubProtocol => null;

        public async Task ReceiveTextAsync(TransportFrame frame)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
            await this.received.Writer.WriteAsync(new Received(bytes, WebSocketMessageType.Text));
        }

        public async Task<TransportFrame> ReadSentFrameAsync()
        {
            using var cts = new CancellationTokenSource(TestTimeout);
            while (true)
            {
                var message = await this.sent.Reader.ReadAsync(cts.Token);
                if (message.MessageType == WebSocketMessageType.Text)
                {
                    var frame = JsonSerializer.Deserialize<TransportFrame>(message.Payload)!;
                    if (frame.Type == TransportFrame.Types.Keepalive)
                    {
                        continue;
                    }

                    return frame;
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
            await this.sent.Writer.WriteAsync(new Sent(payload, messageType), cancellationToken);
        }

        private sealed record Received(byte[] Payload, WebSocketMessageType MessageType);

        private sealed record Sent(byte[] Payload, WebSocketMessageType MessageType);
    }
}
