using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Transport.Http;

public sealed class HttpTransport : ITransport
{
    private static readonly TimeSpan ReadDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly WebSocket socket;
    private readonly ConcurrentDictionary<string, HttpTransportChannel> channels = new();
    private readonly ConcurrentDictionary<string, HttpTransportStream> streams = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly CancellationTokenSource keepaliveShutdown = new();
    private readonly Task readLoop;
    private readonly Task keepaliveLoop;
    private int disposed;

    public HttpTransport(WebSocket socket, TimeSpan? keepaliveInterval = null)
    {
        this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
        this.readLoop = Task.Run(this.ReadLoopAsync);
        this.keepaliveLoop = Task.Run(() => this.KeepaliveLoopAsync(keepaliveInterval ?? TimeSpan.FromSeconds(30)));
    }

    public async Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        var channelId = Guid.NewGuid().ToString("D");
        var channel = new HttpTransportChannel(this, channelId);
        this.channels[channelId] = channel;

        try
        {
            await this.SendFrameAsync(new TransportFrame
            {
                Type = TransportFrame.Types.ChannelOpen,
                ChannelId = channelId,
                Request = request.Clone(),
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            this.channels.TryRemove(channelId, out _);
            throw;
        }

        return channel;
    }

    public async Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        var streamId = Guid.NewGuid().ToString("D");
        var stream = new HttpTransportStream(this, streamId);
        this.streams[streamId] = stream;

        try
        {
            await this.SendFrameAsync(new TransportFrame
            {
                Type = TransportFrame.Types.StreamOpen,
                StreamId = streamId,
                Request = request.Clone(),
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            this.streams.TryRemove(streamId, out _);
            throw;
        }

        return stream;
    }

    internal void NotifyStreamDisposed(string streamId)
    {
        if (this.streams.TryRemove(streamId, out _) && Volatile.Read(ref this.disposed) == 0)
        {
            _ = this.SendStreamCloseAsync(streamId, CancellationToken.None);
        }
    }

    internal Task SendChannelMessageAsync(string channelId, JsonElement payload, CancellationToken ct)
        => this.SendFrameAsync(new TransportFrame
        {
            Type = TransportFrame.Types.ChannelMessage,
            ChannelId = channelId,
            Payload = payload.Clone(),
        }, ct);

    internal Task SendChannelCloseAsync(string channelId, CancellationToken ct)
        => this.SendFrameAsync(new TransportFrame { Type = TransportFrame.Types.ChannelClose, ChannelId = channelId }, ct);

    internal Task SendStreamDataAsync(string streamId, ReadOnlyMemory<byte> payload, CancellationToken ct)
        => this.SendFrameAsync(new TransportFrame
        {
            Type = TransportFrame.Types.StreamData,
            StreamId = streamId,
            Data = Convert.ToBase64String(payload.Span),
        }, ct);

    internal Task SendStreamCloseAsync(string streamId, CancellationToken ct)
        => this.SendFrameAsync(new TransportFrame { Type = TransportFrame.Types.StreamClose, StreamId = streamId }, ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (this.socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await this.SendFrameAsync(new TransportFrame { Type = TransportFrame.Types.TransportClose }, CancellationToken.None).ConfigureAwait(false);
                await this.socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "disposed", CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
        }

        this.keepaliveShutdown.Cancel();

        // Force the read loop to exit while letting it drain any frames already
        // buffered on the socket first. Disposing the socket signals EOF /
        // aborts pending ReceiveAsync calls, so the read loop dispatches any
        // remaining inbound frames (delivering them to their channels/streams)
        // before returning. Only AFTER that draining is complete do we call
        // CompleteAll, so channels never lose an inbound ChannelMessage /
        // streams never lose an inbound StreamData that had already been
        // received when dispose was requested.
        try
        {
            this.socket.Dispose();
        }
        catch
        {
        }

        try
        {
            await this.readLoop.WaitAsync(ReadDrainTimeout).ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await this.keepaliveLoop.WaitAsync(ReadDrainTimeout).ConfigureAwait(false);
        }
        catch
        {
        }

        this.CompleteAll(new ObjectDisposedException(nameof(HttpTransport)));
        this.keepaliveShutdown.Dispose();
        this.sendLock.Dispose();
    }

    private async Task SendFrameAsync(TransportFrame frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
        await this.sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await this.socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            this.sendLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            // Loop until the socket returns a Close frame or ReceiveAsync throws
            // (which happens when the socket is disposed / aborted). Do NOT gate
            // on this.socket.State: DisposeAsync calls socket.Dispose() to force
            // this loop to unblock, but any frames already buffered on the
            // transport (e.g. a ChannelMessage the peer sent right before
            // TransportClose) must still be dispatched to their channels before
            // we exit. A state-based check races with dispose and drops those
            // buffered frames, which is the #1183 dispose race.
            while (true)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await this.socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (Volatile.Read(ref this.disposed) == 0)
                        {
                            this.CompleteAll(new TransportException("HTTP transport closed."));
                        }

                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    await this.DispatchFrameAsync(JsonSerializer.Deserialize<TransportFrame>(message.ToArray())!).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref this.disposed) == 0)
            {
                this.CompleteAll(ex);
            }
        }
    }

    private async Task DispatchFrameAsync(TransportFrame frame)
    {
        switch (frame.Type)
        {
            case TransportFrame.Types.ChannelMessage:
                if (frame.ChannelId is not null && this.channels.TryGetValue(frame.ChannelId, out var channel) && frame.Payload is { } payload)
                {
                    await channel.ReceiveAsync(payload).ConfigureAwait(false);
                }
                break;
            case TransportFrame.Types.ChannelOpenError:
                if (frame.ChannelId is not null)
                {
                    var error = new TransportException(frame.Message ?? "Channel open failed.");
                    if (this.channels.TryRemove(frame.ChannelId, out var failedChannel))
                    {
                        failedChannel.Complete(error);
                    }
                }
                else if (frame.StreamId is not null && this.streams.TryRemove(frame.StreamId, out var failedStream))
                {
                    failedStream.Fault(new TransportException(frame.Message ?? "Stream open failed."));
                }
                break;
            case TransportFrame.Types.ChannelClose:
                if (frame.ChannelId is not null && this.channels.TryRemove(frame.ChannelId, out var closed))
                {
                    closed.Complete();
                }
                break;
            case TransportFrame.Types.StreamData:
                if (frame.StreamId is not null
                    && frame.Data is not null
                    && this.streams.TryGetValue(frame.StreamId, out var streamForData))
                {
                    await streamForData.ReceiveAsync(Convert.FromBase64String(frame.Data), CancellationToken.None).ConfigureAwait(false);
                }
                break;
            case TransportFrame.Types.StreamClose:
                if (frame.StreamId is not null && this.streams.TryRemove(frame.StreamId, out var closedStream))
                {
                    closedStream.Complete();
                }
                break;
            case TransportFrame.Types.TransportClose:
                this.CompleteAll(new TransportException("Remote transport closed."));
                break;
        }
    }

    private async Task KeepaliveLoopAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(this.keepaliveShutdown.Token).ConfigureAwait(false))
            {
                await this.SendFrameAsync(new TransportFrame { Type = TransportFrame.Types.Keepalive }, this.keepaliveShutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void CompleteAll(Exception ex)
    {
        foreach (var channel in this.channels.Values)
        {
            channel.Complete(ex);
        }

        foreach (var stream in this.streams.Values)
        {
            stream.Fault(ex);
        }

        this.channels.Clear();
        this.streams.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref this.disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(HttpTransport));
        }
    }
}
