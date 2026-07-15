namespace Phantom.Workspaces.Transport.Mcp;

public sealed class McpServerSession : IAsyncDisposable
{
    private readonly IMessageChannel channel;
    private readonly IAsyncDisposable? inner;
    private int disposed;

    internal McpServerSession(IMessageChannel channel, IAsyncDisposable? inner)
    {
        this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
        this.inner = inner;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        if (this.inner is not null)
        {
            await this.inner.DisposeAsync().ConfigureAwait(false);
        }

        await this.channel.DisposeAsync().ConfigureAwait(false);
    }
}
