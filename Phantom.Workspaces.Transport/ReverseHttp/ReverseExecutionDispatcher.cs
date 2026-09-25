using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Transport.Logging;

namespace Phantom.Workspaces.Transport.ReverseHttp;

public enum ReverseDispatchStopReason
{
    Stopped,
    ChannelClosed,
    DispatchFailed,
}

public readonly record struct ReverseDispatchOutcome(ReverseDispatchStopReason Reason, string? ExceptionType = null);

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
    private readonly TransportPeerIdentityProvider? peerIdentities;
    private readonly Func<string, CancellationToken, Task>? registrationInfoHandler;
    private readonly ILogger logger;
    private readonly bool traceMetadata;
    private readonly ConcurrentDictionary<string, string> attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DispatchedChannel> channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingChannelOpen> pendingChannelOpens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DispatchedStream> streams = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IAsyncDisposable> sessions = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task<ReverseDispatchOutcome> readLoop;
    private int sessionsClosed;
    private int disposed;

    public ReverseExecutionDispatcher(
        IMessageChannel registrationChannel,
        TransportRegistry registry,
        TransportPeerIdentityProvider? peerIdentities = null,
        Func<string, CancellationToken, Task>? registrationInfoHandler = null,
        ILoggerFactory? loggerFactory = null,
        TransportMetadataLoggingOptions? metadataLogging = null)
    {
        this.registrationChannel = registrationChannel ?? throw new ArgumentNullException(nameof(registrationChannel));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.peerIdentities = peerIdentities;
        this.registrationInfoHandler = registrationInfoHandler;
        this.logger = loggerFactory?.CreateLogger<ReverseExecutionDispatcher>()
            ?? NullLogger<ReverseExecutionDispatcher>.Instance;
        this.traceMetadata = (metadataLogging ?? TransportMetadataLoggingOptions.FromEnvironment()).Enabled;
        this.readLoop = this.RunAsync();
    }

    public Task<ReverseDispatchOutcome> Completion => this.readLoop;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        await this.shutdown.CancelAsync().ConfigureAwait(false);
        await this.readLoop.ConfigureAwait(false);
        this.shutdown.Dispose();
    }

    private async Task<ReverseDispatchOutcome> RunAsync()
    {
        var outcome = new ReverseDispatchOutcome(ReverseDispatchStopReason.ChannelClosed);
        try
        {
            await foreach (var frame in this.registrationChannel.Reader.ReadAllAsync(this.shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    await this.DispatchAsync(frame).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    outcome = this.shutdown.IsCancellationRequested
                        ? new ReverseDispatchOutcome(ReverseDispatchStopReason.Stopped)
                        : new ReverseDispatchOutcome(ReverseDispatchStopReason.DispatchFailed, error.GetType().Name);
                    break;
                }
            }

            if (this.shutdown.IsCancellationRequested)
            {
                outcome = new ReverseDispatchOutcome(ReverseDispatchStopReason.Stopped);
            }
        }
        catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
        {
            outcome = new ReverseDispatchOutcome(ReverseDispatchStopReason.Stopped);
        }
        catch (Exception error) when (this.registrationChannel.Reader.Completion.IsCompleted)
        {
            outcome = new ReverseDispatchOutcome(ReverseDispatchStopReason.ChannelClosed, error.GetType().Name);
        }
        catch (Exception error)
        {
            outcome = new ReverseDispatchOutcome(ReverseDispatchStopReason.DispatchFailed, error.GetType().Name);
        }
        finally
        {
            await this.CloseHostedSessionsAsync().ConfigureAwait(false);
            this.logger.LogInformation(
                "Reverse worker dispatch closed; outcome {Outcome}; exception-type {ExceptionType}.",
                outcome.Reason, outcome.ExceptionType ?? "none");
        }

        return outcome;
    }

    private async Task CloseHostedSessionsAsync()
    {
        if (Interlocked.Exchange(ref this.sessionsClosed, 1) != 0)
        {
            return;
        }

        foreach (var channel in this.channels.Values)
        {
            channel.CompleteIncoming();
        }

        foreach (var pending in this.pendingChannelOpens.Values)
        {
            pending.Cancel();
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
        this.pendingChannelOpens.Clear();
        this.streams.Clear();
        this.sessions.Clear();
        this.attempts.Clear();
    }

    private async Task DispatchAsync(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object
            || !frame.TryGetProperty("type", out var typeProperty)
            || typeProperty.GetString() is not { } type)
        {
            return;
        }

        if (this.traceMetadata && this.logger.IsEnabled(LogLevel.Debug))
        {
            this.logger.LogDebug(
                "Reverse worker dispatch; attempt {Attempt}; frame {FrameType}; bytes {Bytes}; outcome received-at-worker.",
                this.MarkerFor(frame), TransportMetadataTrace.FrameType(frame),
                TransportMetadataTrace.ByteCount(frame));
        }

        switch (type)
        {
            case "reverse-registration-info":
                if (this.registrationInfoHandler is not null
                    && frame.TryGetProperty("hub-profile-entity-id", out var hubProfileEntityId)
                    && hubProfileEntityId.GetString() is { Length: > 0 } hubId)
                {
                    await this.registrationInfoHandler(hubId, this.shutdown.Token).ConfigureAwait(false);
                }

                break;

            case "channel-open":
                this.StartChannelOpen(frame);
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

    private string MarkerFor(JsonElement frame)
    {
        if (frame.TryGetProperty("channelId", out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { } id)
        {
            if (TransportMetadataTrace.FrameType(frame) == "channel-open")
                return this.attempts.GetOrAdd(id, _ => TransportMetadataTrace.MarkerForRequest(frame));
            if (TransportMetadataTrace.FrameType(frame) == "channel-close"
                && this.attempts.TryRemove(id, out var closedMarker))
                return closedMarker;
            if (this.attempts.TryGetValue(id, out var marker))
                return marker;
        }
        return TransportMetadataTrace.NewMarker();
    }

    private void StartChannelOpen(JsonElement frame)
    {
        if (!TryGetId(frame, "channelId", out var channelId)
            || !frame.TryGetProperty("request", out var request))
        {
            return;
        }

        var channel = new DispatchedChannel(this.registrationChannel.Writer, channelId);
        var pending = new PendingChannelOpen(
            channel,
            CancellationTokenSource.CreateLinkedTokenSource(this.shutdown.Token));
        if (!this.pendingChannelOpens.TryAdd(channelId, pending))
        {
            pending.Dispose();
            _ = this.SendChannelOpenErrorAsync(
                channelId,
                "duplicate-channel",
                "A channel with this identifier is already opening.");
            return;
        }

        this.channels[channelId] = channel;
        if (this.peerIdentities is not null && TryReadAuthenticatedPeer(frame, out var peer))
            this.peerIdentities.SetIdentity(channel, peer);

        _ = this.CompleteChannelOpenAsync(channelId, request.Clone(), pending);
    }

    private async Task CompleteChannelOpenAsync(
        string channelId,
        JsonElement request,
        PendingChannelOpen pending)
    {
        try
        {
            var session = await this.registry
                .OnChannelOpenAsync(request, pending.Channel, pending.Token)
                .ConfigureAwait(false);
            if (session is null)
            {
                this.channels.TryRemove(
                    new KeyValuePair<string, DispatchedChannel>(channelId, pending.Channel));
                pending.Channel.CompleteIncoming();
                await this.SendChannelOpenErrorAsync(
                        channelId,
                        "no-listener",
                        "No transport listener accepted the channel open request.")
                    .ConfigureAwait(false);
                return;
            }

            if (pending.Token.IsCancellationRequested
                || !this.channels.TryGetValue(channelId, out var active)
                || !ReferenceEquals(active, pending.Channel))
            {
                await session.DisposeAsync().ConfigureAwait(false);
                return;
            }

            this.sessions[channelId] = session;
            if (pending.Token.IsCancellationRequested
                || !this.channels.TryGetValue(channelId, out active)
                || !ReferenceEquals(active, pending.Channel))
            {
                if (this.sessions.TryRemove(
                        new KeyValuePair<string, IAsyncDisposable>(channelId, session)))
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (pending.Token.IsCancellationRequested)
        {
            this.channels.TryRemove(
                new KeyValuePair<string, DispatchedChannel>(channelId, pending.Channel));
            pending.Channel.CompleteIncoming();
        }
        catch (Exception)
        {
            this.channels.TryRemove(
                new KeyValuePair<string, DispatchedChannel>(channelId, pending.Channel));
            pending.Channel.CompleteIncoming();
            await this.SendChannelOpenErrorAsync(
                    channelId,
                    "listener-error",
                    "The remote channel listener failed to open the channel.")
                .ConfigureAwait(false);
        }
        finally
        {
            if (this.pendingChannelOpens.TryGetValue(channelId, out var current)
                && ReferenceEquals(current, pending))
            {
                this.pendingChannelOpens.TryRemove(channelId, out _);
            }

            pending.Dispose();
        }
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
        if (this.pendingChannelOpens.TryRemove(channelId, out var pending))
        {
            pending.Cancel();
        }

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
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(
                new Dictionary<string, string>
            {
                ["type"] = "channel-open-error",
                ["channelId"] = channelId,
                ["error-code"] = code,
                ["message"] = message,
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

    private static bool TryReadAuthenticatedPeer(JsonElement frame, out TransportPeerIdentity identity)
    {
        identity = null!;
        if (!frame.TryGetProperty("authenticatedPeer", out var peer)
            || peer.ValueKind != JsonValueKind.Object
            || !peer.TryGetProperty("authenticationScheme", out var scheme)
            || !peer.TryGetProperty("stablePeerId", out var stable)
            || string.IsNullOrWhiteSpace(scheme.GetString())
            || string.IsNullOrWhiteSpace(stable.GetString()))
            return false;
        identity = new TransportPeerIdentity
        {
            AuthenticationScheme = scheme.GetString()!,
            StablePeerId = stable.GetString()!,
            UserEntityId = peer.TryGetProperty("userEntityId", out var user) ? user.GetString() : null,
            UserComputerProfileEntityId = peer.TryGetProperty("userComputerProfileEntityId", out var profile)
                ? profile.GetString()
                : null,
        };
        return true;
    }

    private sealed class PendingChannelOpen(
        DispatchedChannel channel,
        CancellationTokenSource cancellation) : IDisposable
    {
        private readonly object gate = new();
        private bool disposed;

        public DispatchedChannel Channel { get; } = channel;

        public CancellationToken Token => cancellation.Token;

        public void Cancel()
        {
            lock (this.gate)
            {
                if (!this.disposed)
                {
                    cancellation.Cancel();
                }
            }
        }

        public void Dispose()
        {
            lock (this.gate)
            {
                if (this.disposed)
                {
                    return;
                }

                this.disposed = true;
                cancellation.Dispose();
            }
        }
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
