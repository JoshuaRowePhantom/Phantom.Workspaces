using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Transport.Logging;
using System.Diagnostics;
using System.Threading.Channels;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Transport.ReverseHttp;

public sealed class ReverseHttpServerTransportFactory : ITransportListener
{
    private readonly ConcurrentDictionary<string, RegisteredClient> registrations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> inFlightCounts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ReverseConnectionStatusRegistry? statusRegistry;
    private readonly bool requireAuthenticatedRelays;
    private readonly EntityId? hubProfileEntityId;
    private readonly ILogger logger;
    private readonly bool traceMetadata;

    public ReverseHttpServerTransportFactory()
        : this(null)
    {
    }

    public ReverseHttpServerTransportFactory(
        ReverseConnectionStatusRegistry? statusRegistry,
        bool requireAuthenticatedRelays = false,
        EntityId? hubProfileEntityId = null,
        ILoggerFactory? loggerFactory = null,
        TransportMetadataLoggingOptions? metadataLogging = null)
    {
        this.statusRegistry = statusRegistry;
        this.requireAuthenticatedRelays = requireAuthenticatedRelays;
        this.hubProfileEntityId = hubProfileEntityId;
        this.logger = loggerFactory?.CreateLogger<ReverseHttpServerTransportFactory>()
            ?? NullLogger<ReverseHttpServerTransportFactory>.Instance;
        this.traceMetadata = (metadataLogging ?? TransportMetadataLoggingOptions.FromEnvironment()).Enabled;
    }

    public int RegistrationCount => this.registrations.Count;

    public bool IsRegistered(string entityId) => this.registrations.ContainsKey(entityId);

    public async ValueTask DisconnectRegistrationAsync(string entityId)
    {
        if (this.registrations.TryRemove(entityId, out var registration))
        {
            this.inFlightCounts.TryRemove(entityId, out _);
            this.statusRegistry?.OnUnregistered(entityId);
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<ITransport?> ConnectToRegisteredAsync(
        string entityId,
        TransportPeerIdentity authenticatedPeer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentNullException.ThrowIfNull(authenticatedPeer);
        if (!this.registrations.TryGetValue(entityId, out var registration))
        {
            return null;
        }

        var local = await registration.AttachLocalAsync(authenticatedPeer, cancellationToken).ConfigureAwait(false);
        this.OnRelayOpened(entityId);
        return new RegisteredTransport(
            new ReverseHttpTransport(local.ClientChannel, authenticatedPeer),
            new RelayLease(this, entityId, local.Relay));
    }

    public async Task<IAsyncDisposable?> OnChannelOpenAsync(
        JsonElement request,
        IMessageChannel channel,
        CancellationToken ct = default)
    {
        if (!request.TryGetProperty("type", out var typeProperty)
            || typeProperty.GetString() is not { } type)
        {
            return null;
        }

        if (string.Equals(type, "reverse-register", StringComparison.OrdinalIgnoreCase))
        {
            var entityId = ReadEntityId(request);
            var registration = new RegisteredClient(channel, this.logger, this.traceMetadata);
            if (this.registrations.TryGetValue(entityId, out var previous))
            {
                this.registrations[entityId] = registration;
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            else if (!this.registrations.TryAdd(entityId, registration))
            {
                previous = this.registrations[entityId];
                this.registrations[entityId] = registration;
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            this.inFlightCounts[entityId] = 0;
            this.statusRegistry?.OnRegistered(entityId, DateTimeOffset.UtcNow, ReadAnnouncedEndpoint(request));
            this.logger.LogInformation("Reverse registration accepted; outcome registered.");
            if (this.hubProfileEntityId is { } hubId)
            {
                using var registrationInfo = JsonDocument.Parse(
                    JsonSerializer.Serialize(
                        new Dictionary<string, string>
                        {
                            ["type"] = "reverse-registration-info",
                            ["hub-profile-entity-id"] = hubId.ToString(),
                        }));
                await channel.Writer.WriteAsync(registrationInfo.RootElement.Clone(), ct).ConfigureAwait(false);
            }

            return new RegistrationLease(this, entityId, registration);
        }

        if (string.Equals(type, "reverse-http", StringComparison.OrdinalIgnoreCase))
        {
            var entityId = ReadEntityId(request);
            var authenticatedPeer = TryReadAuthenticatedPeer(request);
            if (this.requireAuthenticatedRelays && authenticatedPeer is null)
            {
                return await ErrorLease.CreateAsync(
                    channel,
                    "unauthorized",
                    "An authenticated transport peer is required for reverse HTTP relay.",
                    ct).ConfigureAwait(false);
            }

            if (this.registrations.TryGetValue(entityId, out var registration))
            {
                IAsyncDisposable relay = authenticatedPeer is not null
                    ? await registration.AttachAsync(channel, authenticatedPeer, ct).ConfigureAwait(false)
                    : await registration.AttachLegacyAsync(channel, ct).ConfigureAwait(false);
                this.OnRelayOpened(entityId);
                this.logger.LogInformation("Reverse relay accepted; outcome attached.");
                return relay is RegisteredClient.AttachedRelay attached
                    ? new RelayLease(this, entityId, attached)
                    : new LegacyRelayLease(this, entityId, (RelaySession)relay);
            }

            return await ErrorLease.CreateAsync(
                channel,
                "not-registered",
                $"No reverse HTTP registration exists for '{entityId}'.",
                ct).ConfigureAwait(false);
        }

        return null;
    }

    public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
        => Task.FromResult<IAsyncDisposable?>(null);

    public async ValueTask DisposeAsync()
    {
        var clients = this.registrations.Values.ToArray();
        this.registrations.Clear();
        this.inFlightCounts.Clear();
        foreach (var client in clients)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnRelayOpened(string entityId)
    {
        var count = this.inFlightCounts.AddOrUpdate(entityId, 1, static (_, current) => current + 1);
        this.statusRegistry?.OnInFlightChanged(entityId, count);
    }

    private void OnRelayClosed(string entityId)
    {
        if (!this.inFlightCounts.ContainsKey(entityId))
        {
            return;
        }

        var count = this.inFlightCounts.AddOrUpdate(entityId, 0, static (_, current) => current > 0 ? current - 1 : 0);
        this.statusRegistry?.OnInFlightChanged(entityId, count);
    }

    private async ValueTask RemoveRegistrationAsync(
        string entityId,
        RegisteredClient registration)
    {
        if (this.registrations.TryRemove(new KeyValuePair<string, RegisteredClient>(entityId, registration)))
        {
            this.inFlightCounts.TryRemove(entityId, out _);
            this.statusRegistry?.OnUnregistered(entityId);
            this.logger.LogInformation("Reverse registration closed; outcome disconnected.");
        }

        await registration.DisposeAsync().ConfigureAwait(false);
    }

    private static string ReadEntityId(JsonElement request)
    {
        if (!request.TryGetProperty("entity-id", out var entityIdProperty)
            || entityIdProperty.GetString() is not { Length: > 0 } entityId)
        {
            throw new TransportException("Reverse HTTP descriptors must include entity-id.");
        }

        return entityId;
    }

    private static string? ReadAnnouncedEndpoint(JsonElement request)
        => request.TryGetProperty("announced-endpoint", out var endpointProperty)
            && endpointProperty.GetString() is { Length: > 0 } endpoint
            ? endpoint
            : null;

    private static TransportPeerIdentity? TryReadAuthenticatedPeer(JsonElement request)
    {
        if (!request.TryGetProperty("authenticated-peer", out var peer)
            || peer.ValueKind != JsonValueKind.Object
            || !peer.TryGetProperty("authentication-scheme", out var scheme)
            || !peer.TryGetProperty("stable-peer-id", out var stable)
            || string.IsNullOrWhiteSpace(scheme.GetString())
            || string.IsNullOrWhiteSpace(stable.GetString()))
        {
            return null;
        }

        return new TransportPeerIdentity
        {
            AuthenticationScheme = scheme.GetString()!,
            StablePeerId = stable.GetString()!,
            UserEntityId = ReadOptionalString(peer, "user-entity-id"),
            UserComputerProfileEntityId = ReadOptionalString(peer, "user-computer-profile-entity-id"),
        };
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private sealed class RegistrationLease(
        ReverseHttpServerTransportFactory factory,
        string entityId,
        RegisteredClient registration) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => factory.RemoveRegistrationAsync(entityId, registration);
    }

    private sealed class RelayLease(
        ReverseHttpServerTransportFactory factory,
        string entityId,
        RegisteredClient.AttachedRelay relay) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await relay.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                factory.OnRelayClosed(entityId);
            }
        }
    }

    private sealed class LegacyRelayLease(
        ReverseHttpServerTransportFactory factory,
        string entityId,
        RelaySession relay) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await relay.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                factory.OnRelayClosed(entityId);
            }
        }
    }

    private sealed class RegisteredTransport(
        ReverseHttpTransport inner,
        RelayLease relayLease) : ITransport
    {
        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
            => inner.ConnectToMessageChannelAsync(request, ct);

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => inner.ConnectToStreamAsync(request, ct);

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await relayLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ErrorLease(IMessageChannel channel) : IAsyncDisposable
    {
        public static async Task<ErrorLease> CreateAsync(
            IMessageChannel channel,
            string code,
            string message,
            CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(
                JsonSerializer.Serialize(
                    new Dictionary<string, string>
                    {
                        ["type"] = "channel-open-error",
                        ["error-code"] = code,
                        ["message"] = message,
                    }));
            await channel.Writer.WriteAsync(document.RootElement.Clone(), cancellationToken).ConfigureAwait(false);
            return new ErrorLease(channel);
        }

        public ValueTask DisposeAsync()
        {
            channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RegisteredClient : IAsyncDisposable
    {
        private readonly IMessageChannel registrationChannel;
        private readonly ILogger logger;
        private readonly bool traceMetadata;
        private readonly ConcurrentDictionary<string, AttachedRelay> relays = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource shutdown = new();
        private readonly object readLoopGate = new();
        private Task? readLoop;
        private int disposed;

        public RegisteredClient(IMessageChannel registrationChannel, ILogger logger, bool traceMetadata)
        {
            this.registrationChannel = registrationChannel;
            this.logger = logger;
            this.traceMetadata = traceMetadata;
        }

        public async Task<IAsyncDisposable> AttachLegacyAsync(
            IMessageChannel relayChannel,
            CancellationToken cancellationToken)
        {
            using var ackDocument = JsonDocument.Parse("""{"type":"channel-open-ack"}""");
            await relayChannel.Writer.WriteAsync(ackDocument.RootElement.Clone(), cancellationToken).ConfigureAwait(false);
            return new RelaySession(relayChannel, this.registrationChannel, cancellationToken,
                this.logger, this.traceMetadata);
        }

        public async Task<AttachedRelay> AttachAsync(
            IMessageChannel relayChannel,
            TransportPeerIdentity? authenticatedPeer,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
            this.EnsureReadLoopStarted();
            var relay = new AttachedRelay(this, relayChannel, authenticatedPeer, this.logger, this.traceMetadata);
            if (!this.relays.TryAdd(relay.Prefix, relay))
            {
                throw new TransportException("Could not allocate a reverse HTTP relay.");
            }

            await relay.StartAsync(cancellationToken).ConfigureAwait(false);
            return relay;
        }

        public async Task<LocalAttachment> AttachLocalAsync(
            TransportPeerIdentity authenticatedPeer,
            CancellationToken cancellationToken)
        {
            var (serverChannel, clientChannel) = PairedMessageChannel.Create();
            var relay = await this.AttachAsync(serverChannel, authenticatedPeer, cancellationToken).ConfigureAwait(false);
            return new LocalAttachment(relay, clientChannel);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
            }

            // Snapshot before cancellation: each relay's linked token stops its input pump, whose
            // finally block removes the relay from this dictionary. Taking the snapshot afterward
            // can therefore lose the relay before DisposeAsync sends channel-close to its caller,
            // leaving a pre-SessionCreated Copilot turn stuck forever (#1594).
            var relays = this.relays.Values.ToArray();
            await this.shutdown.CancelAsync().ConfigureAwait(false);
            foreach (var relay in relays)
            {
                await relay.DisposeAsync().ConfigureAwait(false);
            }

            this.relays.Clear();
            await this.registrationChannel.DisposeAsync().ConfigureAwait(false);
            if (this.readLoop is not null)
            {
                await this.readLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            this.shutdown.Dispose();
        }

        public async Task WriteToRegistrationAsync(JsonElement frame, CancellationToken cancellationToken)
            => await this.registrationChannel.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);

        public void Remove(string prefix)
            => this.relays.TryRemove(prefix, out _);

        private async Task RunReadLoopAsync()
        {
            try
            {
                await foreach (var frame in this.registrationChannel.Reader.ReadAllAsync(this.shutdown.Token).ConfigureAwait(false))
                {
                    if (TryFindRelay(frame, out var prefix, out var relay))
                    {
                        var response = RewriteCorrelationId(frame, prefix, addPrefix: false);
                        var started = Stopwatch.GetTimestamp();
                        await relay.WriteResponseAsync(response, this.shutdown.Token)
                            .ConfigureAwait(false);
                        relay.TraceResponse(response, started);
                    }
                    else if (this.relays.Count == 1)
                    {
                        var onlyRelay = this.relays.Values.Single();
                        var started = Stopwatch.GetTimestamp();
                        await onlyRelay.WriteResponseAsync(frame.Clone(), this.shutdown.Token)
                            .ConfigureAwait(false);
                        onlyRelay.TraceResponse(frame, started);
                    }
                }
            }
            catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
            {
            }
            catch (ChannelClosedException)
            {
            }
            finally
            {
                foreach (var relay in this.relays.Values.ToArray())
                {
                    await relay.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private void EnsureReadLoopStarted()
        {
            lock (this.readLoopGate)
            {
                this.readLoop ??= this.RunReadLoopAsync();
            }
        }

        private bool TryFindRelay(JsonElement frame, out string prefix, out AttachedRelay relay)
        {
            foreach (var propertyName in CorrelationPropertyNames)
            {
                if (!frame.TryGetProperty(propertyName, out var property)
                    || property.GetString() is not { } correlationId)
                {
                    continue;
                }

                var separator = correlationId.IndexOf(':', StringComparison.Ordinal);
                if (separator > 0)
                {
                    prefix = correlationId[..separator];
                    return this.relays.TryGetValue(prefix, out relay!);
                }
            }

            prefix = string.Empty;
            relay = null!;
            return false;
        }

        public sealed class AttachedRelay : IAsyncDisposable
        {
            private readonly RegisteredClient owner;
            private readonly IMessageChannel channel;
            private readonly TransportPeerIdentity? authenticatedPeer;
            private readonly CancellationTokenSource shutdown;
            private readonly ConcurrentDictionary<string, byte> channelIds = new(StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string, byte> streamIds = new(StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string, string> attempts = new(StringComparer.Ordinal);
            private readonly ILogger logger;
            private readonly bool traceMetadata;
            private Task? pump;
            private int disposed;

            public AttachedRelay(
                RegisteredClient owner,
                IMessageChannel channel,
                TransportPeerIdentity? authenticatedPeer,
                ILogger logger,
                bool traceMetadata)
            {
                this.owner = owner;
                this.channel = channel;
                this.authenticatedPeer = authenticatedPeer;
                this.logger = logger;
                this.traceMetadata = traceMetadata;
                this.Prefix = Guid.NewGuid().ToString("N");
                this.shutdown = CancellationTokenSource.CreateLinkedTokenSource(owner.shutdown.Token);
            }

            public string Prefix { get; }

            public IMessageChannel Channel => this.channel;

            public async Task StartAsync(CancellationToken cancellationToken)
            {
                using var ackDocument = JsonDocument.Parse("""{"type":"channel-open-ack"}""");
                await this.channel.Writer.WriteAsync(ackDocument.RootElement.Clone(), cancellationToken).ConfigureAwait(false);
                this.pump = this.RunInputLoopAsync();
            }

            public async Task WriteResponseAsync(JsonElement frame, CancellationToken cancellationToken)
                => await this.channel.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref this.disposed, 1) != 0)
                {
                    return;
                }

                this.logger.LogInformation("Reverse relay closed; outcome disconnected.");
                this.owner.Remove(this.Prefix);
                await this.shutdown.CancelAsync().ConfigureAwait(false);
                if (this.pump is not null)
                {
                    await this.pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                await this.CloseLogicalConnectionsAsync().ConfigureAwait(false);
                await this.channel.DisposeAsync().ConfigureAwait(false);
                this.shutdown.Dispose();
            }

            private async Task RunInputLoopAsync()
            {
                try
                {
                    await foreach (var frame in this.channel.Reader.ReadAllAsync(this.shutdown.Token).ConfigureAwait(false))
                    {
                        this.TrackCorrelationId(frame);
                        var marker = this.MarkerFor(frame);
                        var routed = RewriteCorrelationId(frame, this.Prefix, addPrefix: true, this.authenticatedPeer);
                        var started = Stopwatch.GetTimestamp();
                        await this.owner.WriteToRegistrationAsync(routed, this.shutdown.Token).ConfigureAwait(false);
                        this.TraceHop("caller-to-worker", marker, frame, started);
                    }
                }
                catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
                {
                }
                catch (ChannelClosedException)
                {
                }
                finally
                {
                    this.owner.Remove(this.Prefix);
                }
            }

            private void TrackCorrelationId(JsonElement frame)
            {
                if (!frame.TryGetProperty("type", out var typeProperty)
                    || typeProperty.GetString() is not { } type)
                {
                    return;
                }

                if (frame.TryGetProperty("channelId", out var channelIdProperty)
                    && channelIdProperty.GetString() is { } channelId)
                {
                    if (type == "channel-close")
                    {
                        this.channelIds.TryRemove(channelId, out _);
                    }
                    else
                    {
                        this.channelIds[channelId] = 0;
                    }
                }

                if (frame.TryGetProperty("streamId", out var streamIdProperty)
                    && streamIdProperty.GetString() is { } streamId)
                {
                    if (type == "stream-close")
                    {
                        this.streamIds.TryRemove(streamId, out _);
                    }
                    else
                    {
                        this.streamIds[streamId] = 0;
                    }
                }
            }

            public void TraceResponse(JsonElement frame, long started)
                => this.TraceHop("worker-to-caller", this.MarkerFor(frame), frame, started);

            private string MarkerFor(JsonElement frame)
            {
                if (frame.ValueKind == JsonValueKind.Object
                    && frame.TryGetProperty("channelId", out var channelId)
                    && channelId.ValueKind == JsonValueKind.String
                    && channelId.GetString() is { } id)
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

            private void TraceHop(string direction, string marker, JsonElement frame, long started)
            {
                if (this.traceMetadata && this.logger.IsEnabled(LogLevel.Debug))
                    this.logger.LogDebug(
                        "Reverse relay hop {Direction}; attempt {Attempt}; frame {FrameType}; bytes {Bytes}; outcome written-to-hop; elapsed {ElapsedMilliseconds}ms.",
                        direction, marker, TransportMetadataTrace.FrameType(frame),
                        TransportMetadataTrace.ByteCount(frame),
                        Math.Clamp((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, int.MaxValue));
            }

            private async Task CloseLogicalConnectionsAsync()
            {
                try
                {
                    using var closeDocument = JsonDocument.Parse("""{"type":"channel-close"}""");
                    await this.channel.Writer.WriteAsync(closeDocument.RootElement.Clone()).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                }
                catch (InvalidOperationException)
                {
                }

                foreach (var channelId in this.channelIds.Keys)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(
                            JsonSerializer.Serialize(new { type = "channel-close", channelId }));
                        await this.channel.Writer.WriteAsync(document.RootElement.Clone()).ConfigureAwait(false);
                    }
                    catch (ChannelClosedException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                foreach (var streamId in this.streamIds.Keys)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(
                            JsonSerializer.Serialize(new { type = "stream-close", streamId }));
                        await this.channel.Writer.WriteAsync(document.RootElement.Clone()).ConfigureAwait(false);
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

        public sealed record LocalAttachment(AttachedRelay Relay, IMessageChannel ClientChannel);
    }

    private sealed class PairedMessageChannel(
        Channel<JsonElement> incoming,
        Channel<JsonElement> outgoing) : IMessageChannel
    {
        public ChannelWriter<JsonElement> Writer => outgoing.Writer;

        public ChannelReader<JsonElement> Reader => incoming.Reader;

        public static (PairedMessageChannel Server, PairedMessageChannel Client) Create()
        {
            var serverIncoming = Channel.CreateUnbounded<JsonElement>();
            var clientIncoming = Channel.CreateUnbounded<JsonElement>();
            return (
                new PairedMessageChannel(serverIncoming, clientIncoming),
                new PairedMessageChannel(clientIncoming, serverIncoming));
        }

        public ValueTask DisposeAsync()
        {
            incoming.Writer.TryComplete();
            outgoing.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private static readonly string[] CorrelationPropertyNames = ["channelId", "channel-id", "streamId", "stream-id"];

    private static JsonElement RewriteCorrelationId(
        JsonElement frame,
        string prefix,
        bool addPrefix,
        TransportPeerIdentity? authenticatedPeer = null)
    {
        var node = JsonNode.Parse(frame.GetRawText())?.AsObject()
            ?? throw new TransportException("Reverse HTTP relay frames must be JSON objects.");
        foreach (var propertyName in CorrelationPropertyNames)
        {
            if (node[propertyName]?.GetValue<string>() is not { } correlationId)
            {
                continue;
            }

            node[propertyName] = addPrefix
                ? $"{prefix}:{correlationId}"
                : correlationId[(prefix.Length + 1)..];
            break;
        }

        if (addPrefix
            && authenticatedPeer is not null
            && string.Equals(node["type"]?.GetValue<string>(), "channel-open", StringComparison.Ordinal)
            && node["authenticatedPeer"] is null)
        {
            node["authenticatedPeer"] = JsonSerializer.SerializeToNode(
                new
                {
                    authenticationScheme = authenticatedPeer.AuthenticationScheme,
                    stablePeerId = authenticatedPeer.StablePeerId,
                    userEntityId = authenticatedPeer.UserEntityId,
                    userComputerProfileEntityId = authenticatedPeer.UserComputerProfileEntityId,
                });
        }

        return JsonSerializer.SerializeToElement(node);
    }
}
