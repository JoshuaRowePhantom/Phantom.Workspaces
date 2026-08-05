using System.Net.WebSockets;
using System.Threading.Channels;

namespace Phantom.Workspaces.Transport.Tests.Infrastructure;

/// <summary>
/// In-process paired <see cref="WebSocket"/> implementation. Two instances share
/// two unbounded channels: A's sends become B's receives, and vice versa. This
/// lets us drive a real <see cref="Phantom.Workspaces.Transport.Http.HttpTransport"/>
/// against a real <see cref="Phantom.Workspaces.Transport.Http.ServerHttpTransport"/>
/// without opening a TCP socket.
/// </summary>
public sealed class PairedWebSocket : WebSocket
{
    private readonly Channel<Frame> incoming;
    private readonly Channel<Frame> outgoing;
    private WebSocketState state = WebSocketState.Open;

    private PairedWebSocket(Channel<Frame> incoming, Channel<Frame> outgoing)
    {
        this.incoming = incoming;
        this.outgoing = outgoing;
    }

    public override WebSocketCloseStatus? CloseStatus => null;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State => this.state;

    public override string? SubProtocol => null;

    public static (PairedWebSocket client, PairedWebSocket server) CreatePair()
    {
        var aToB = Channel.CreateUnbounded<Frame>();
        var bToA = Channel.CreateUnbounded<Frame>();
        var client = new PairedWebSocket(incoming: bToA, outgoing: aToB);
        var server = new PairedWebSocket(incoming: aToB, outgoing: bToA);
        return (client, server);
    }

    public override void Abort()
    {
        this.state = WebSocketState.Aborted;
        this.incoming.Writer.TryComplete();
        this.outgoing.Writer.TryComplete();
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        this.state = WebSocketState.Closed;
        this.outgoing.Writer.TryComplete();
        this.incoming.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        this.state = WebSocketState.CloseSent;
        this.outgoing.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        this.state = WebSocketState.Closed;
        this.outgoing.Writer.TryComplete();
        this.incoming.Writer.TryComplete();
    }

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            var frame = await this.incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            frame.Payload.CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(frame.Payload.Length, frame.MessageType, true);
        }
        catch (ChannelClosedException)
        {
            this.state = WebSocketState.Closed;
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        var payload = buffer.AsSpan().ToArray();
        try
        {
            await this.outgoing.Writer.WriteAsync(new Frame(payload, messageType), cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
    }

    private sealed record Frame(byte[] Payload, WebSocketMessageType MessageType);
}
