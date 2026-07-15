using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Transport.Http;

public sealed class HttpTransport : ITransport
{
    private readonly WebSocket socket;
    private readonly ConcurrentDictionary<string, HttpTransportChannel> channels = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IMessageChannel>> pendingChannels = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task readLoop;
    private readonly Task keepaliveLoop;
    private bool disposed;

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
        var pending = new TaskCompletionSource<IMessageChannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.channels[channelId] = channel;
        this.pendingChannels[channelId] = pending;

        await this.SendFrameAsync(new TransportFrame
        {
            Type = TransportFrame.Types.ChannelOpen,
            ChannelId = channelId,
            Request = request.Clone(),
        }, ct).ConfigureAwait(false);

        await using var registration = ct.Register(() => pending.TrySetCanceled(ct));
        return await pending.Task.ConfigureAwait(false);
    }

    public async Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();
        var streamId = Guid.NewGuid().ToString("D");
        var stream = new HttpTransportStream(this, streamId);

        await this.SendFrameAsync(new TransportFrame
        {
            Type = TransportFrame.Types.StreamOpen,
            StreamId = streamId,
            Request = request.Clone(),
        }, ct).ConfigureAwait(false);

        return stream;
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
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.shutdown.Cancel();
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

        this.CompleteAll(new ObjectDisposedException(nameof(HttpTransport)));
        this.socket.Dispose();
        this.shutdown.Dispose();
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
            while (!this.shutdown.IsCancellationRequested && this.socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await this.socket.ReceiveAsync(buffer, this.shutdown.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        this.CompleteAll(new TransportException("HTTP transport closed."));
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.CompleteAll(ex);
        }
    }

    private async Task DispatchFrameAsync(TransportFrame frame)
    {
        switch (frame.Type)
        {
            case TransportFrame.Types.ChannelMessage:
                if (frame.ChannelId is not null && this.channels.TryGetValue(frame.ChannelId, out var channel) && frame.Payload is { } payload)
                {
                    if (this.pendingChannels.TryRemove(frame.ChannelId, out var pending))
                    {
                        pending.TrySetResult(channel);
                    }

                    await channel.ReceiveAsync(payload).ConfigureAwait(false);
                }
                break;
            case TransportFrame.Types.ChannelOpenError:
                if (frame.ChannelId is not null)
                {
                    var error = new TransportException(frame.Message ?? "Channel open failed.");
                    if (this.pendingChannels.TryRemove(frame.ChannelId, out var pending))
                    {
                        pending.TrySetException(error);
                    }

                    if (this.channels.TryRemove(frame.ChannelId, out var failedChannel))
                    {
                        failedChannel.Complete(error);
                    }
                }
                break;
            case TransportFrame.Types.ChannelClose:
                if (frame.ChannelId is not null && this.channels.TryRemove(frame.ChannelId, out var closed))
                {
                    closed.Complete();
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
            while (await timer.WaitForNextTickAsync(this.shutdown.Token).ConfigureAwait(false))
            {
                await this.SendFrameAsync(new TransportFrame { Type = TransportFrame.Types.Keepalive }, this.shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CompleteAll(Exception ex)
    {
        foreach (var pending in this.pendingChannels.Values)
        {
            pending.TrySetException(ex);
        }

        foreach (var channel in this.channels.Values)
        {
            channel.Complete(ex);
        }

        this.pendingChannels.Clear();
        this.channels.Clear();
    }

    private void ThrowIfDisposed()
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(HttpTransport));
        }
    }
}
