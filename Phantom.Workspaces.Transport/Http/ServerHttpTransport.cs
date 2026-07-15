using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Transport.Http;

public sealed class ServerHttpTransport : IAsyncDisposable
{
    private readonly WebSocket socket;
    private readonly TransportRegistry registry;
    private readonly TimeSpan leaseDuration;
    private readonly ConcurrentDictionary<string, ChannelLease> channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StreamLease> streams = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private long lastFrameTicks = DateTimeOffset.UtcNow.UtcTicks;
    private int disposed;

    public ServerHttpTransport(WebSocket socket, TransportRegistry registry, TimeSpan? leaseDuration = null)
    {
        this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.leaseDuration = leaseDuration ?? TimeSpan.FromSeconds(90);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this.shutdown.Token);
        var leaseTask = this.LeaseLoopAsync(linked.Token);
        try
        {
            await this.ReadLoopAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            await this.DisposeAsync().ConfigureAwait(false);
            await leaseTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
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

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested && this.socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await this.socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Text)
            {
                Volatile.Write(ref this.lastFrameTicks, DateTimeOffset.UtcNow.UtcTicks);
                await this.DispatchFrameAsync(JsonSerializer.Deserialize<TransportFrame>(message.ToArray())!, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchFrameAsync(TransportFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case TransportFrame.Types.ChannelOpen:
                await this.OpenChannelAsync(frame, cancellationToken).ConfigureAwait(false);
                break;
            case TransportFrame.Types.ChannelMessage:
                if (frame.ChannelId is not null
                    && frame.Payload is { } payload
                    && this.channels.TryGetValue(frame.ChannelId, out var channelLease))
                {
                    await channelLease.Channel.ReceiveAsync(payload).ConfigureAwait(false);
                }
                break;
            case TransportFrame.Types.ChannelClose:
                if (frame.ChannelId is not null && this.channels.TryRemove(frame.ChannelId, out var channel))
                {
                    channel.Channel.Complete();
                    await channel.Lease.DisposeAsync().ConfigureAwait(false);
                }
                break;
            case TransportFrame.Types.StreamOpen:
                await this.OpenStreamAsync(frame, cancellationToken).ConfigureAwait(false);
                break;
            case TransportFrame.Types.StreamData:
                if (frame.StreamId is not null
                    && frame.Data is not null
                    && this.streams.TryGetValue(frame.StreamId, out var streamLease))
                {
                    await streamLease.Stream.ReceiveAsync(Convert.FromBase64String(frame.Data), cancellationToken).ConfigureAwait(false);
                }
                break;
            case TransportFrame.Types.StreamClose:
                if (frame.StreamId is not null && this.streams.TryRemove(frame.StreamId, out var stream))
                {
                    stream.Stream.Complete();
                    await stream.Lease.DisposeAsync().ConfigureAwait(false);
                }
                break;
            case TransportFrame.Types.TransportClose:
                await this.DisposeAsync().ConfigureAwait(false);
                break;
        }
    }

    private async Task OpenChannelAsync(TransportFrame frame, CancellationToken cancellationToken)
    {
        if (frame.ChannelId is not { Length: > 0 } channelId || frame.Request is not { } request)
        {
            return;
        }

        var channel = new ServerMessageChannel(this, channelId);
        var lease = await this.registry.OnChannelOpenAsync(request, channel, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            channel.Complete();
            await this.SendFrameAsync(new TransportFrame
            {
                Type = TransportFrame.Types.ChannelOpenError,
                ChannelId = channelId,
                ErrorCode = "not-found",
                Message = "No listener handled the channel request.",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        this.channels[channelId] = new ChannelLease(channel, lease);
    }

    private async Task OpenStreamAsync(TransportFrame frame, CancellationToken cancellationToken)
    {
        if (frame.StreamId is not { Length: > 0 } streamId || frame.Request is not { } request)
        {
            return;
        }

        var stream = new ServerTransportStream(this, streamId);
        var lease = await this.registry.OnStreamOpenAsync(request, stream, cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            stream.Complete();
            await this.SendFrameAsync(new TransportFrame
            {
                Type = TransportFrame.Types.ChannelOpenError,
                StreamId = streamId,
                ErrorCode = "not-found",
                Message = "No listener handled the stream request.",
            }, cancellationToken).ConfigureAwait(false);
            return;
        }

        this.streams[streamId] = new StreamLease(stream, lease);
    }

    private async Task LeaseLoopAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(10, this.leaseDuration.TotalMilliseconds / 3));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var elapsed = DateTimeOffset.UtcNow - new DateTimeOffset(Volatile.Read(ref this.lastFrameTicks), TimeSpan.Zero);
                if (elapsed >= this.leaseDuration)
                {
                    await this.SendFrameAsync(new TransportFrame { Type = TransportFrame.Types.TransportClose }, CancellationToken.None).ConfigureAwait(false);
                    await this.DisposeAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendFrameAsync(TransportFrame frame, CancellationToken ct)
    {
        if (this.socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        this.shutdown.Cancel();
        foreach (var (_, channel) in this.channels)
        {
            channel.Channel.Complete();
            await channel.Lease.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var (_, stream) in this.streams)
        {
            stream.Stream.Complete();
            await stream.Lease.DisposeAsync().ConfigureAwait(false);
        }

        this.channels.Clear();
        this.streams.Clear();
        if (this.socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await this.socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        this.socket.Dispose();
        this.shutdown.Dispose();
        this.sendLock.Dispose();
    }

    private sealed record ChannelLease(ServerMessageChannel Channel, IAsyncDisposable Lease);

    private sealed record StreamLease(ServerTransportStream Stream, IAsyncDisposable Lease);

    private sealed class ServerMessageChannel(ServerHttpTransport transport, string channelId) : IMessageChannel
    {
        private readonly Channel<JsonElement> inbound = Channel.CreateUnbounded<JsonElement>();

        public ChannelWriter<JsonElement> Writer => new ForwardingWriter(transport, channelId);

        public ChannelReader<JsonElement> Reader => this.inbound.Reader;

        public ValueTask ReceiveAsync(JsonElement payload) => this.inbound.Writer.WriteAsync(payload.Clone());

        public void Complete(Exception? exception = null) => this.inbound.Writer.TryComplete(exception);

        public async ValueTask DisposeAsync()
        {
            this.Complete();
            await transport.SendChannelCloseAsync(channelId, CancellationToken.None).ConfigureAwait(false);
        }

        private sealed class ForwardingWriter(ServerHttpTransport transport, string channelId) : ChannelWriter<JsonElement>
        {
            public override bool TryWrite(JsonElement item)
            {
                _ = this.WriteAsync(item);
                return true;
            }

            public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
                => ValueTask.FromResult(true);

            public override async ValueTask WriteAsync(JsonElement item, CancellationToken cancellationToken = default)
                => await transport.SendChannelMessageAsync(channelId, item, cancellationToken).ConfigureAwait(false);
        }
    }
}
