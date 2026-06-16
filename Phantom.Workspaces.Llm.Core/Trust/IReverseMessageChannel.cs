using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// A duplex frame channel for the reverse-execution protocol. The transport (a WebSocket, or an
/// in-memory pair for tests/in-process) implements send/receive of <see cref="ReverseFrame"/>s.
/// <see cref="ReceiveAsync"/> returns <see langword="null"/> when the channel is closed.
/// </summary>
public interface IReverseMessageChannel : IAsyncDisposable
{
    Task SendAsync(ReverseFrame frame, CancellationToken cancellationToken);

    Task<ReverseFrame?> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A pair of in-memory <see cref="IReverseMessageChannel"/> endpoints connected back-to-back: a
/// frame sent on one end is received on the other. Used by the in-process end-to-end path and tests
/// without a real WebSocket.
/// </summary>
public sealed class InMemoryReverseMessageChannelPair
{
    private readonly Channel<ReverseFrame> serverToClient = Channel.CreateUnbounded<ReverseFrame>();
    private readonly Channel<ReverseFrame> clientToServer = Channel.CreateUnbounded<ReverseFrame>();

    /// <summary>The server (connected-to instance) endpoint: sends to client, receives from client.</summary>
    public IReverseMessageChannel ServerEnd => new Endpoint(this.serverToClient, this.clientToServer);

    /// <summary>The client (connecting instance) endpoint: sends to server, receives from server.</summary>
    public IReverseMessageChannel ClientEnd => new Endpoint(this.clientToServer, this.serverToClient);

    private sealed class Endpoint : IReverseMessageChannel
    {
        private readonly Channel<ReverseFrame> outbound;
        private readonly Channel<ReverseFrame> inbound;

        public Endpoint(Channel<ReverseFrame> outbound, Channel<ReverseFrame> inbound)
        {
            this.outbound = outbound;
            this.inbound = inbound;
        }

        public async Task SendAsync(ReverseFrame frame, CancellationToken cancellationToken)
        {
            await this.outbound.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ReverseFrame?> ReceiveAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await this.inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            this.outbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
