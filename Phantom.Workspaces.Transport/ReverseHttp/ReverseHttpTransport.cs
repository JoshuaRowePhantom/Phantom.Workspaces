using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Transport.ReverseHttp;

/// <summary>
/// Client-side transport that multiplexes many logical message channels over a single reverse-HTTP
/// registration or relay channel. Outbound logical writes are wrapped as <c>channel-message</c>
/// frames; a background read loop demultiplexes inbound frames back to the originating logical
/// channel by <c>channelId</c>, completing duplex round-trips.
/// </summary>
public sealed class ReverseHttpTransport : ITransport
{
    private readonly IMessageChannel registrationChannel;
    private readonly ConcurrentDictionary<string, ReverseHttpMessageChannel> channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DispatchedStream> streams = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = new();

    // Completes with null once the relay is acknowledged (channel-open-ack) or with a TransportException
    // when the relay reports a channel-open-error or closes before it is established. Never faults the
    // task itself, so callers that never observe it (e.g. the direct registration path) do not produce
    // unobserved-exception warnings.
    private readonly TaskCompletionSource<TransportException?> relayReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task readLoop;

    public ReverseHttpTransport(IMessageChannel registrationChannel)
    {
        this.registrationChannel = registrationChannel ?? throw new ArgumentNullException(nameof(registrationChannel));
        this.readLoop = this.RunReadLoopAsync();
    }

    public async Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
    {
        var channelId = Guid.NewGuid().ToString("D");
        var channel = new ReverseHttpMessageChannel(this, channelId);
        this.channels[channelId] = channel;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "channel-open",
            channelId,
            request = JsonSerializer.Deserialize<JsonElement>(request.GetRawText()),
        }));
        await this.registrationChannel.Writer.WriteAsync(document.RootElement.Clone(), ct).ConfigureAwait(false);
        return channel;
    }

    public async Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
    {
        var streamId = Guid.NewGuid().ToString("D");
        var stream = new DispatchedStream(this.registrationChannel.Writer, streamId);
        this.streams[streamId] = stream;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "stream-open",
            streamId,
            request = JsonSerializer.Deserialize<JsonElement>(request.GetRawText()),
        }));
        await this.registrationChannel.Writer.WriteAsync(document.RootElement.Clone(), ct).ConfigureAwait(false);
        return stream;
    }

    /// <summary>
    /// Waits until the reverse-HTTP relay is established (the hub acknowledged the relay) or throws a
    /// <see cref="TransportException"/> if the relay reported a <c>channel-open-error</c> (for example
    /// because the target machine is not registered) or closed before it was established.
    /// </summary>
    public async Task WaitForRelayEstablishedAsync(CancellationToken ct = default)
    {
        var error = await this.relayReady.Task.WaitAsync(ct).ConfigureAwait(false);
        if (error is not null)
        {
            throw error;
        }
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

        this.relayReady.TrySetResult(new TransportException("Reverse HTTP transport disposed before the relay was established."));
        this.shutdown.Dispose();
    }

    private async Task RunReadLoopAsync()
    {
        try
        {
            await foreach (var frame in this.registrationChannel.Reader.ReadAllAsync(this.shutdown.Token).ConfigureAwait(false))
            {
                this.Dispatch(frame);
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
        finally
        {
            this.relayReady.TrySetResult(new TransportException("Reverse HTTP relay channel closed before it was established."));
            foreach (var channel in this.channels.Values)
            {
                channel.CompleteIncoming();
            }

            foreach (var stream in this.streams.Values)
            {
                stream.CompleteIncoming();
            }
        }
    }

    private void Dispatch(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object
            || !frame.TryGetProperty("type", out var typeProperty)
            || typeProperty.GetString() is not { } type)
        {
            return;
        }

        switch (type)
        {
            case "channel-open-ack":
                this.relayReady.TrySetResult(null);
                break;

            case "channel-open-error":
                var code = frame.TryGetProperty("error-code", out var codeProperty) ? codeProperty.GetString() : null;
                var message = frame.TryGetProperty("message", out var messageProperty) ? messageProperty.GetString() : null;
                this.relayReady.TrySetResult(new TransportException(
                    message ?? $"Reverse HTTP relay rejected the connection: {code ?? "unknown"}."));
                break;

            case "channel-message":
                if (TryGetChannelId(frame, out var messageChannelId)
                    && this.channels.TryGetValue(messageChannelId, out var target)
                    && frame.TryGetProperty("payload", out var payload))
                {
                    target.DeliverIncoming(payload.Clone());
                }

                break;

            case "channel-close":
                if (TryGetChannelId(frame, out var closeChannelId)
                    && this.channels.TryRemove(closeChannelId, out var closing))
                {
                    closing.CompleteIncoming();
                }

                break;

            case "stream-data":
                if (TryGetStreamId(frame, out var dataStreamId)
                    && this.streams.TryGetValue(dataStreamId, out var dataStream)
                    && frame.TryGetProperty("data", out var dataProperty)
                    && dataProperty.GetString() is { } base64)
                {
                    dataStream.DeliverIncoming(Convert.FromBase64String(base64));
                }

                break;

            case "stream-close":
                if (TryGetStreamId(frame, out var closeStreamId)
                    && this.streams.TryRemove(closeStreamId, out var closingStream))
                {
                    closingStream.CompleteIncoming();
                }

                break;
        }
    }

    private static bool TryGetStreamId(JsonElement frame, out string streamId)
    {
        if (frame.TryGetProperty("streamId", out var streamIdProperty)
            && streamIdProperty.GetString() is { Length: > 0 } value)
        {
            streamId = value;
            return true;
        }

        streamId = string.Empty;
        return false;
    }

    private static bool TryGetChannelId(JsonElement frame, out string channelId)
    {
        if (frame.TryGetProperty("channelId", out var channelIdProperty)
            && channelIdProperty.GetString() is { Length: > 0 } value)
        {
            channelId = value;
            return true;
        }

        channelId = string.Empty;
        return false;
    }

    private sealed class ReverseHttpMessageChannel : IMessageChannel
    {
        private readonly ReverseHttpTransport owner;
        private readonly string channelId;
        private readonly Channel<JsonElement> incoming = Channel.CreateUnbounded<JsonElement>();

        public ReverseHttpMessageChannel(ReverseHttpTransport owner, string channelId)
        {
            this.owner = owner;
            this.channelId = channelId;
            this.Writer = new MultiplexingChannelWriter(owner.registrationChannel.Writer, channelId);
        }

        public ChannelWriter<JsonElement> Writer { get; }

        public ChannelReader<JsonElement> Reader => this.incoming.Reader;

        public void DeliverIncoming(JsonElement payload) => this.incoming.Writer.TryWrite(payload);

        public void CompleteIncoming() => this.incoming.Writer.TryComplete();

        public async ValueTask DisposeAsync()
        {
            this.incoming.Writer.TryComplete();
            this.owner.channels.TryRemove(this.channelId, out _);
            try
            {
                using var document = JsonDocument.Parse($$"""{"type":"channel-close","channelId":"{{this.channelId}}"}""");
                await this.owner.registrationChannel.Writer.WriteAsync(document.RootElement.Clone()).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
