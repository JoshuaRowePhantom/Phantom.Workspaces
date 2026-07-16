using System.Text.Json;

namespace Phantom.Workspaces.Transport.Mcp;

public sealed class McpClientOverTransport : IAsyncDisposable
{
    private readonly ITransport transport;
    private readonly JsonElement request;
    private IMessageChannel? channel;

    public McpClientOverTransport(ITransport transport, JsonElement mcpConnectionRequest)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.request = mcpConnectionRequest.Clone();
    }

    public IMessageChannel Channel => this.channel ?? throw new InvalidOperationException("MCP channel has not been opened.");

    public async Task OpenAsync(CancellationToken ct = default)
    {
        if (this.channel is not null)
        {
            return;
        }

        this.channel = await this.transport.ConnectToMessageChannelAsync(this.request, ct).ConfigureAwait(false);
    }

    public async ValueTask SendAsync(JsonElement message, CancellationToken ct = default)
    {
        await this.EnsureOpenAsync(ct).ConfigureAwait(false);
        await this.Channel.Writer.WriteAsync(message.Clone(), ct).ConfigureAwait(false);
    }

    public async ValueTask<JsonElement> ReadAsync(CancellationToken ct = default)
    {
        await this.EnsureOpenAsync(ct).ConfigureAwait(false);
        var message = await this.Channel.Reader.ReadAsync(ct).ConfigureAwait(false);
        return message.Clone();
    }

    public async ValueTask DisposeAsync()
    {
        if (this.channel is not null)
        {
            await this.channel.DisposeAsync().ConfigureAwait(false);
            this.channel = null;
        }
    }

    private Task EnsureOpenAsync(CancellationToken ct) => this.channel is null ? this.OpenAsync(ct) : Task.CompletedTask;
}
