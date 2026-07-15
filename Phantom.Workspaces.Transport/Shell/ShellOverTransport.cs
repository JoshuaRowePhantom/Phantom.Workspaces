using System.Text.Json;

namespace Phantom.Workspaces.Transport.Shell;

public sealed class ShellOverTransport : IAsyncDisposable
{
    private readonly ITransport transport;
    private readonly JsonElement shellRequest;
    private Stream? stream;

    public ShellOverTransport(ITransport transport, JsonElement shellRequest)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.shellRequest = shellRequest.Clone();
    }

    public Stream Stream => this.stream ?? throw new InvalidOperationException("Shell stream has not been opened.");

    public async Task OpenAsync(CancellationToken ct = default)
    {
        if (this.stream is not null)
        {
            return;
        }

        this.stream = await this.transport.ConnectToStreamAsync(this.shellRequest, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (this.stream is not null)
        {
            await this.stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
