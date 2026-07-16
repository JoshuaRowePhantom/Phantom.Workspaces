using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Transport.ReverseHttp;

/// <summary>
/// Executor-side host that services a reverse-HTTP registration channel. It reads relayed
/// <c>channel-open</c> / <c>stream-open</c> frames (forwarded by the hub from a remote forwarding
/// client) and dispatches them to the local transport listeners registered in the supplied
/// <see cref="TransportRegistry"/> (for example <c>ChatClientTransportListener</c>,
/// <c>McpTransportListener</c> and <c>ShellTransportListener</c>), wiring inbound
/// <c>channel-message</c> frames back to the accepted logical channel and multiplexing the
/// listener's outbound writes back over the single registration channel.
/// </summary>
public sealed class ReverseExecutionDispatcher : IAsyncDisposable
{
    private readonly IMessageChannel registrationChannel;
    private readonly TransportRegistry registry;
    private readonly ConcurrentDictionary<string, DispatchedChannel> channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DispatchedStream> streams = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IAsyncDisposable> sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task readLoop;

    public ReverseExecutionDispatcher(IMessageChannel registrationChannel, TransportRegistry registry)
    {
        this.registrationChannel = registrationChannel ?? throw new ArgumentNullException(nameof(registrationChannel));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.readLoop = this.RunAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await this.shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await this.readLoop.ConfigureAwait(false);
        }
        catch
        {
        }

        foreach (var channel in this.channels.Values)
        {
            channel.CompleteIncoming();
        }

        foreach (var stream in this.streams.Values)
        {
            stream.CompleteIncoming();
        }

        foreach (var session in this.sessions.Values)
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        this.channels.Clear();
        this.streams.Clear();
        this.sessions.Clear();
        this.shutdown.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var frame in this.registrationChannel.Reader.ReadAllAsync(this.shutdown.Token).ConfigureAwait(false))
            {
                await this.DispatchAsync(frame).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task DispatchAsync(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object
            || !frame.TryGetProperty("type", out var typeProperty)
            || typeProperty.GetString() is not { } type)
        {
            return;
        }

        switch (type)
        {
            case "channel-open":
                await this.HandleChannelOpenAsync(frame).ConfigureAwait(false);
                break;

            case "channel-message":
                if (TryGetId(frame, "channelId", out var messageChannelId)
                    && this.channels.TryGetValue(messageChannelId, out var target)
                    && frame.TryGetProperty("payload", out var payload))
                {
                    target.DeliverIncoming(payload.Clone());
                }

                break;

            case "channel-close":
                if (TryGetId(frame, "channelId", out var closeChannelId))
                {
                    await this.CloseChannelAsync(closeChannelId).ConfigureAwait(false);
                }

                break;

            case "stream-open":
                await this.HandleStreamOpenAsync(frame).ConfigureAwait(false);
                break;

            case "stream-data":
                if (TryGetId(frame, "streamId", out var dataStreamId)
                    && this.streams.TryGetValue(dataStreamId, out var dataStream)
                    && frame.TryGetProperty("data", out var dataProperty)
                    && dataProperty.GetString() is { } base64)
                {
                    dataStream.DeliverIncoming(Convert.FromBase64String(base64));
                }

                break;

            case "stream-close":
                if (TryGetId(frame, "streamId", out var closeStreamId))
                {
                    await this.CloseStreamAsync(closeStreamId).ConfigureAwait(false);
                }

                break;
        }
    }

    private async Task HandleChannelOpenAsync(JsonElement frame)
    {
        if (!TryGetId(frame, "channelId", out var channelId)
            || !frame.TryGetProperty("request", out var request))
        {
            return;
        }

        var channel = new DispatchedChannel(this.registrationChannel.Writer, channelId);
        this.channels[channelId] = channel;

        var session = await this.registry.OnChannelOpenAsync(request.Clone(), channel, this.shutdown.Token).ConfigureAwait(false);
        if (session is null)
        {
            this.channels.TryRemove(channelId, out _);
            channel.CompleteIncoming();
            await this.SendChannelOpenErrorAsync(channelId, "no-listener", "No transport listener accepted the channel open request.").ConfigureAwait(false);
            return;
        }

        this.sessions[channelId] = session;
    }

    private async Task HandleStreamOpenAsync(JsonElement frame)
    {
        if (!TryGetId(frame, "streamId", out var streamId)
            || !frame.TryGetProperty("request", out var request))
        {
            return;
        }

        var stream = new DispatchedStream(this.registrationChannel.Writer, streamId);
        this.streams[streamId] = stream;
        var session = await this.registry.OnStreamOpenAsync(request.Clone(), stream, this.shutdown.Token).ConfigureAwait(false);
        if (session is null)
        {
            this.streams.TryRemove(streamId, out _);
            await stream.DisposeAsync().ConfigureAwait(false);
            await this.SendChannelOpenErrorAsync(streamId, "no-listener", "No transport listener accepted the stream open request.").ConfigureAwait(false);
            return;
        }

        this.sessions[streamId] = session;
    }

    private async Task CloseStreamAsync(string streamId)
    {
        if (this.streams.TryRemove(streamId, out var stream))
        {
            stream.CompleteIncoming();
        }

        if (this.sessions.TryRemove(streamId, out var session))
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task CloseChannelAsync(string channelId)
    {
        if (this.channels.TryRemove(channelId, out var channel))
        {
            channel.CompleteIncoming();
        }

        if (this.sessions.TryRemove(channelId, out var session))
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task SendChannelOpenErrorAsync(string channelId, string code, string message)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                type = "channel-open-error",
                channelId,
                errorCode = code,
                message,
            }));
            await this.registrationChannel.Writer.WriteAsync(document.RootElement.Clone()).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool TryGetId(JsonElement frame, string propertyName, out string value)
    {
        if (frame.TryGetProperty(propertyName, out var property)
            && property.GetString() is { Length: > 0 } id)
        {
            value = id;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private sealed class DispatchedChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> incoming = Channel.CreateUnbounded<JsonElement>();

        public DispatchedChannel(ChannelWriter<JsonElement> outbound, string channelId)
        {
            this.Writer = new MultiplexingChannelWriter(outbound, channelId);
        }

        public ChannelWriter<JsonElement> Writer { get; }

        public ChannelReader<JsonElement> Reader => this.incoming.Reader;

        public void DeliverIncoming(JsonElement payload) => this.incoming.Writer.TryWrite(payload);

        public void CompleteIncoming() => this.incoming.Writer.TryComplete();

        public ValueTask DisposeAsync()
        {
            this.incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
