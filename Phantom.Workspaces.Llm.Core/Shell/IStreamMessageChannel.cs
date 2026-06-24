using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Llm.Shell;

/// <summary>
/// A duplex frame channel for the shell transport. The carrier (a WebSocket, the binary reverse-frame
/// variant, or an in-memory pair for tests/in-process) implements send/receive of
/// <see cref="StreamFrame"/>s. <see cref="ReceiveAsync"/> returns <see langword="null"/> when the
/// channel is closed. This mirrors <c>IReverseMessageChannel</c>; consumers above the executor only
/// ever see the demultiplexed <see cref="System.IO.Stream"/> exposed by <see cref="ShellSession"/>.
/// </summary>
public interface IStreamMessageChannel : IAsyncDisposable
{
    Task SendAsync(StreamFrame frame, CancellationToken cancellationToken);

    Task<StreamFrame?> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A pair of in-memory <see cref="IStreamMessageChannel"/> endpoints connected back-to-back: a frame
/// sent on one end is received on the other. Used by the in-process local carrier and tests without a
/// real WebSocket or PTY.
/// </summary>
public sealed class InMemoryStreamMessageChannelPair
{
    private readonly Channel<StreamFrame> hostToClient = Channel.CreateUnbounded<StreamFrame>();
    private readonly Channel<StreamFrame> clientToHost = Channel.CreateUnbounded<StreamFrame>();

    /// <summary>The client (initiating) endpoint: writes input to the host, reads output from the host.</summary>
    public IStreamMessageChannel ClientEnd => new Endpoint(this.clientToHost, this.hostToClient);

    /// <summary>The host (process-running) endpoint: writes output to the client, reads input from the client.</summary>
    public IStreamMessageChannel HostEnd => new Endpoint(this.hostToClient, this.clientToHost);

    private sealed class Endpoint : IStreamMessageChannel
    {
        private readonly Channel<StreamFrame> outbound;
        private readonly Channel<StreamFrame> inbound;

        public Endpoint(Channel<StreamFrame> outbound, Channel<StreamFrame> inbound)
        {
            this.outbound = outbound;
            this.inbound = inbound;
        }

        public async Task SendAsync(StreamFrame frame, CancellationToken cancellationToken)
        {
            await this.outbound.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        public async Task<StreamFrame?> ReceiveAsync(CancellationToken cancellationToken)
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
