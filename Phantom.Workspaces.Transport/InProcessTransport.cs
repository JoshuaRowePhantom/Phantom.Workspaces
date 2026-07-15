using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Transport;

/// <summary>
/// In-process transport for testing and local communication.
/// </summary>
public static class InProcessTransport
{
    /// <summary>
    /// Creates a matched pair of ITransport instances connected in-process via channels.
    /// </summary>
    /// <param name="serverRegistry">Optional registry for the server side to dispatch incoming requests.</param>
    /// <returns>A tuple containing the server and client transports.</returns>
    public static (ITransport server, ITransport client) Create(TransportRegistry? serverRegistry = null)
    {
        var serverImpl = new InProcessTransportImpl(serverRegistry);
        var clientImpl = new InProcessTransportImpl(null);

        serverImpl.SetPeer(clientImpl);
        clientImpl.SetPeer(serverImpl);

        return (serverImpl, clientImpl);
    }

    private sealed class InProcessTransportImpl : ITransport
    {
        private InProcessTransportImpl? _peer;
        private readonly Dictionary<string, InProcessMessageChannel> _channels = [];
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _disposed;
        private readonly TransportRegistry? _registry;

        public InProcessTransportImpl(TransportRegistry? registry)
        {
            _registry = registry;
        }

        public void SetPeer(InProcessTransportImpl peer)
        {
            _peer = peer;
        }

        public async Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(InProcessTransportImpl));
            }

            if (_peer is null)
            {
                throw new InvalidOperationException("Peer transport not set.");
            }

            var channelId = Guid.NewGuid().ToString();
            
            var localChannel = new InProcessMessageChannel(channelId, this);
            var remoteChannel = new InProcessMessageChannel(channelId, _peer);

            localChannel.SetPeer(remoteChannel);
            remoteChannel.SetPeer(localChannel);

            await _lock.WaitAsync(ct);
            try
            {
                _channels[channelId] = localChannel;
            }
            finally
            {
                _lock.Release();
            }

            await _peer._lock.WaitAsync(ct);
            try
            {
                _peer._channels[channelId] = remoteChannel;
            }
            finally
            {
                _peer._lock.Release();
            }

            if (_peer._registry is not null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _peer._registry.OnChannelOpenAsync(request, remoteChannel, ct);
                    }
                    catch
                    {
                    }
                });
            }

            return localChannel;
        }

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
        {
            throw new NotImplementedException("Stream support will be added in future iterations.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            await _lock.WaitAsync();
            try
            {
                foreach (var channel in _channels.Values)
                {
                    await channel.DisposeAsync();
                }
                _channels.Clear();
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    private sealed class InProcessMessageChannel : IMessageChannel
    {
        private readonly string _channelId;
        private readonly InProcessTransportImpl _owner;
        private readonly Channel<JsonElement> _inbound = Channel.CreateUnbounded<JsonElement>();
        private InProcessMessageChannel? _peer;
        private bool _disposed;

        public InProcessMessageChannel(string channelId, InProcessTransportImpl owner)
        {
            _channelId = channelId;
            _owner = owner;
        }

        public void SetPeer(InProcessMessageChannel peer)
        {
            _peer = peer;
        }

        public ChannelWriter<JsonElement> Writer => new ForwardingChannelWriter(this);

        public ChannelReader<JsonElement> Reader => _inbound.Reader;

        private sealed class ForwardingChannelWriter : ChannelWriter<JsonElement>
        {
            private readonly InProcessMessageChannel _channel;

            public ForwardingChannelWriter(InProcessMessageChannel channel)
            {
                _channel = channel;
            }

            public override bool TryWrite(JsonElement item)
            {
                if (_channel._peer is null) return false;
                return _channel._peer._inbound.Writer.TryWrite(item);
            }

            public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
            {
                if (_channel._peer is null) return ValueTask.FromResult(false);
                return _channel._peer._inbound.Writer.WaitToWriteAsync(cancellationToken);
            }

            public override async ValueTask WriteAsync(JsonElement item, CancellationToken cancellationToken = default)
            {
                if (_channel._peer is null) throw new InvalidOperationException("Channel not connected");
                await _channel._peer._inbound.Writer.WriteAsync(item, cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _inbound.Writer.Complete();
            }
            catch (ChannelClosedException)
            {
            }
            
            if (_peer is not null && !_peer._disposed)
            {
                try
                {
                    _peer._inbound.Writer.Complete();
                }
                catch (ChannelClosedException)
                {
                }
            }
        }
    }
}

