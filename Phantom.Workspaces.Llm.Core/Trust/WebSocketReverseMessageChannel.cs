using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// An <see cref="IReverseMessageChannel"/> over a <see cref="WebSocket"/>. Frames are serialized as
/// JSON text messages using the Microsoft.Extensions.AI serialization contracts (so
/// <see cref="Microsoft.Extensions.AI.ChatResponseUpdate"/> and message payloads round-trip).
/// Concurrent sends are serialized; a single reader is expected.
/// </summary>
public sealed class WebSocketReverseMessageChannel : IReverseMessageChannel
{
    private static readonly JsonSerializerOptions SerializerOptions = AIJsonUtilities.DefaultOptions;

    private readonly WebSocket socket;
    private readonly bool ownsSocket;
    private readonly SemaphoreSlim sendLock = new(1, 1);

    public WebSocketReverseMessageChannel(WebSocket socket, bool ownsSocket = true)
    {
        this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
        this.ownsSocket = ownsSocket;
    }

    public async Task SendAsync(ReverseFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, SerializerOptions);

        await this.sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await this.socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            this.sendLock.Release();
        }
    }

    public async Task<ReverseFrame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[8192];

        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await this.socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        if (message.Length == 0)
        {
            return null;
        }

        return JsonSerializer.Deserialize<ReverseFrame>(message.ToArray(), SerializerOptions);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (this.socket.State == WebSocketState.Open)
            {
                await this.socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
        }

        if (this.ownsSocket)
        {
            this.socket.Dispose();
        }

        this.sendLock.Dispose();
    }
}
