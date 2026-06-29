using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.Shell;

/// <summary>
/// An <see cref="IStreamMessageChannel"/> that carries <see cref="StreamFrame"/>s over a
/// <see cref="WebSocket"/> as binary messages using the same 5-byte header encoding as
/// <see cref="StreamFramedMessageChannel"/>: <c>[kind: 1 byte][payload length: 4 bytes big-endian][payload bytes]</c>.
/// Concurrent sends are serialized; a single reader is expected.
/// </summary>
public sealed class WebSocketStreamMessageChannel : IStreamMessageChannel
{
    private const int HeaderLength = 5;

    private readonly WebSocket socket;
    private readonly bool ownsSocket;
    private readonly SemaphoreSlim sendLock = new(1, 1);

    public WebSocketStreamMessageChannel(WebSocket socket, bool ownsSocket = true)
    {
        this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
        this.ownsSocket = ownsSocket;
    }

    public async Task SendAsync(StreamFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var payload = frame.Payload;
        var message = new byte[HeaderLength + payload.Length];
        message[0] = (byte)frame.Kind;
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(message.AsMemory(HeaderLength));

        await this.sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            this.sendLock.Release();
        }
    }

    public async Task<StreamFrame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await this.socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            buffer.Write(chunk, 0, result.Count);

            if (result.EndOfMessage)
            {
                break;
            }
        }

        var data = buffer.ToArray();
        if (data.Length < HeaderLength)
        {
            return null;
        }

        var kind = (StreamFrameKind)data[0];
        var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(1, 4));

        ReadOnlyMemory<byte> framePayload = length > 0
            ? data.AsMemory(HeaderLength, (int)length)
            : ReadOnlyMemory<byte>.Empty;

        return new StreamFrame(kind, framePayload);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (this.socket.State == WebSocketState.Open)
            {
                await this.socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
        }

        if (this.ownsSocket)
        {
            this.socket.Dispose();
        }

        this.sendLock.Dispose();
    }
}
