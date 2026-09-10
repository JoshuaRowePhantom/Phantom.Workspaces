using System.Text.Json;
using System.Threading.Channels;

namespace Phantom.Workspaces.Transport.Local;

public sealed class LocalTransport : ITransport
{
    private readonly TransportRegistry registry;
    private readonly Action<IMessageChannel>? authenticateServerChannel;
    private readonly object gate = new();
    private readonly List<LocalMessageChannel> channels = [];
    private readonly List<Task> channelSessions = [];
    private readonly List<Stream> streams = [];
    private bool disposed;

    public LocalTransport(
        TransportRegistry registry,
        Action<IMessageChannel>? authenticateServerChannel = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.authenticateServerChannel = authenticateServerChannel;
    }

    public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
    {
        var (clientChannel, serverChannel) = LocalMessageChannel.CreatePair();
        this.authenticateServerChannel?.Invoke(serverChannel);
        var sessionCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (this.gate)
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException(nameof(LocalTransport));
            }

            this.channels.Add(clientChannel);
            this.channelSessions.Add(sessionCompletion.Task);
        }

        _ = Task.Run(async () =>
        {
            IAsyncDisposable? lease = null;
            try
            {
                lease = await this.registry.OnChannelOpenAsync(request.Clone(), serverChannel, ct).ConfigureAwait(false);
                if (lease is null)
                {
                    clientChannel.Complete(new TransportException("No local listener handled the channel request."));
                    serverChannel.Complete();
                    return;
                }

                await serverChannel.Reader.Completion.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                clientChannel.Complete(ex);
                serverChannel.Complete(ex);
            }
            finally
            {
                if (lease is not null)
                {
                    try
                    {
                        await lease.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }

                await serverChannel.DisposeAsync().ConfigureAwait(false);
                sessionCompletion.TrySetResult();
            }
        }, CancellationToken.None);

        return Task.FromResult<IMessageChannel>(clientChannel);
    }

    public async Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
    {
        this.ThrowIfDisposed();

        var (clientStream, serverStream) = LocalDuplexStream.CreatePair();
        lock (this.gate)
        {
            this.streams.Add(clientStream);
        }

        try
        {
            var lease = await this.registry.OnStreamOpenAsync(request.Clone(), serverStream, ct).ConfigureAwait(false);
            if (lease is null)
            {
                clientStream.SetException(new TransportException("No local listener handled the stream request."));
                await serverStream.DisposeAsync().ConfigureAwait(false);
            }

            return clientStream;
        }
        catch (Exception ex)
        {
            clientStream.SetException(ex);
            await serverStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<LocalMessageChannel> channelSnapshot;
        List<Task> channelSessionSnapshot;
        List<Stream> streamSnapshot;
        lock (this.gate)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            channelSnapshot = [.. this.channels];
            channelSessionSnapshot = [.. this.channelSessions];
            streamSnapshot = [.. this.streams];
            this.channels.Clear();
            this.channelSessions.Clear();
            this.streams.Clear();
        }

        foreach (var channel in channelSnapshot)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var stream in streamSnapshot)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(channelSessionSnapshot).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void ThrowIfDisposed()
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(LocalTransport));
        }
    }

    private sealed class LocalMessageChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> inbound = Channel.CreateUnbounded<JsonElement>();
        private LocalMessageChannel? peer;
        private bool disposed;

        public ChannelWriter<JsonElement> Writer => new ForwardingWriter(this);

        public ChannelReader<JsonElement> Reader => this.inbound.Reader;

        public static (LocalMessageChannel client, LocalMessageChannel server) CreatePair()
        {
            var client = new LocalMessageChannel();
            var server = new LocalMessageChannel();
            client.peer = server;
            server.peer = client;
            return (client, server);
        }

        public void Complete(Exception? exception = null)
        {
            this.inbound.Writer.TryComplete(exception);
        }

        public ValueTask DisposeAsync()
        {
            if (this.disposed)
            {
                return ValueTask.CompletedTask;
            }

            this.disposed = true;
            this.inbound.Writer.TryComplete();
            this.peer?.inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private sealed class ForwardingWriter(LocalMessageChannel owner) : ChannelWriter<JsonElement>
        {
            public override bool TryWrite(JsonElement item) => owner.peer?.inbound.Writer.TryWrite(item.Clone()) ?? false;

            public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
                => owner.peer?.inbound.Writer.WaitToWriteAsync(cancellationToken) ?? ValueTask.FromResult(false);

            public override ValueTask WriteAsync(JsonElement item, CancellationToken cancellationToken = default)
            {
                if (owner.peer is null)
                {
                    throw new InvalidOperationException("Channel is not connected.");
                }

                return owner.peer.inbound.Writer.WriteAsync(item.Clone(), cancellationToken);
            }
        }
    }
}
