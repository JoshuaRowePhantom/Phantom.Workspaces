using System.Text.Json;

namespace Phantom.Workspaces.Transport.Mcp;

public sealed class McpTransportListener : ITransportListener
{
    private readonly Func<JsonElement, IMessageChannel, CancellationToken, Task<IAsyncDisposable?>> openConnectionAsync;

    public McpTransportListener(Func<JsonElement, IMessageChannel, CancellationToken, Task<IAsyncDisposable?>> openConnectionAsync)
    {
        this.openConnectionAsync = openConnectionAsync ?? throw new ArgumentNullException(nameof(openConnectionAsync));
    }

    public Task<IAsyncDisposable?> OnStreamOpenAsync(JsonElement request, Stream stream, CancellationToken ct = default)
        => Task.FromResult<IAsyncDisposable?>(null);

    public async Task<IAsyncDisposable?> OnChannelOpenAsync(JsonElement request, IMessageChannel channel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsType(request, "mcp") || !request.TryGetProperty("connection", out _))
        {
            return null;
        }

        var inner = await this.openConnectionAsync(request.Clone(), channel, ct).ConfigureAwait(false);
        return new McpServerSession(channel, inner);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static bool IsType(JsonElement request, string type)
        => request.ValueKind == JsonValueKind.Object
           && request.TryGetProperty("type", out var typeElement)
           && string.Equals(typeElement.GetString(), type, StringComparison.OrdinalIgnoreCase);
}
