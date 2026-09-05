using System.Text.Json;
using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using McpITransport = ModelContextProtocol.Protocol.ITransport;

namespace Phantom.Workspaces.Transport.Mcp;

/// <summary>
/// Adapter (M2, issue #1438 per-component-executor-binding) that bridges a transport
/// <see cref="IMessageChannel"/> — the <see cref="JsonElement"/>-typed channel returned by
/// <c>ITransportFactoryRegistry.ConnectToAsync</c> — to the MCP SDK client
/// (<c>McpClient.CreateAsync</c>), which requires an <see cref="IClientTransport"/> /
/// <see cref="McpITransport"/>. It is what lets a remote-bound <c>McpToolContextProvider</c> open an
/// <c>McpClientOverTransport</c> over a routed channel: MCP JSON-RPC frames are serialized to (and
/// deserialized from) the <see cref="JsonElement"/> frames the channel carries, using the SDK's
/// <see cref="McpJsonUtilities.DefaultOptions"/> so the JSON-RPC discriminators survive the hop.
/// </summary>
/// <remarks>
/// Reuse-first / no new schema: the frames on the channel ARE the MCP JSON-RPC messages; this adapter
/// only changes their representation (SDK <see cref="JsonRpcMessage"/> ⇄ <see cref="JsonElement"/>).
/// The same channel-backed <see cref="McpITransport"/> is reused host-side by <c>RemoteMcpHostHandler</c>
/// via <see cref="CreateServerTransport"/> to bridge an inbound channel to the real MCP server.
/// </remarks>
public sealed class McpChannelClientTransport : IClientTransport, IAsyncDisposable
{
    private readonly IMessageChannel channel;
    private ChannelBackedTransport? active;
    private int disposed;

    public McpChannelClientTransport(IMessageChannel channel, string? name = null)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        this.Name = string.IsNullOrWhiteSpace(name) ? "mcp-channel" : name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public Task<McpITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
        this.active ??= new ChannelBackedTransport(this.channel);
        return Task.FromResult<McpITransport>(this.active);
    }

    /// <summary>
    /// Creates a bare MCP SDK <see cref="McpITransport"/> that pumps JSON-RPC over the given channel.
    /// Used host-side by the remote MCP host to bridge an inbound channel to the real MCP server
    /// transport (the incoming channel becomes one leg of the JSON-RPC relay).
    /// </summary>
    public static McpITransport CreateServerTransport(IMessageChannel channel)
        => new ChannelBackedTransport(channel ?? throw new ArgumentNullException(nameof(channel)));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        if (this.active is { } transport)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            await this.channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ChannelBackedTransport : McpITransport
    {
        private readonly IMessageChannel channel;
        private readonly Channel<JsonRpcMessage> inbound = System.Threading.Channels.Channel.CreateUnbounded<JsonRpcMessage>();
        private readonly CancellationTokenSource pumpCts = new();
        private readonly Task pumpTask;
        private int disposed;

        public ChannelBackedTransport(IMessageChannel channel)
        {
            this.channel = channel;
            this.pumpTask = Task.Run(this.PumpInboundAsync);
        }

        public string? SessionId => null;

        public ChannelReader<JsonRpcMessage> MessageReader => this.inbound.Reader;

        public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            var element = JsonSerializer.SerializeToElement(message, McpJsonUtilities.DefaultOptions);
            await this.channel.Writer.WriteAsync(element, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) != 0)
            {
                return;
            }

            await this.pumpCts.CancelAsync().ConfigureAwait(false);
            await this.channel.DisposeAsync().ConfigureAwait(false);
            try
            {
                await this.pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            this.inbound.Writer.TryComplete();
            this.pumpCts.Dispose();
        }

        private async Task PumpInboundAsync()
        {
            try
            {
                await foreach (var element in this.channel.Reader.ReadAllAsync(this.pumpCts.Token).ConfigureAwait(false))
                {
                    var message = JsonSerializer.Deserialize<JsonRpcMessage>(element, McpJsonUtilities.DefaultOptions);
                    if (message is not null)
                    {
                        await this.inbound.Writer.WriteAsync(message, this.pumpCts.Token).ConfigureAwait(false);
                    }
                }

                this.inbound.Writer.TryComplete();
            }
            catch (OperationCanceledException)
            {
                this.inbound.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                this.inbound.Writer.TryComplete(ex);
            }
        }
    }
}
