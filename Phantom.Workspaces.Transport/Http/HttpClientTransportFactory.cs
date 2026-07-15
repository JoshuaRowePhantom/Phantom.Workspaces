using System.Net.WebSockets;
using System.Text.Json;

namespace Phantom.Workspaces.Transport.Http;

public sealed class HttpClientTransportFactory : ITransportFactory
{
    public async Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
    {
        if (!connectionDescriptor.TryGetProperty("type", out var type)
            || !string.Equals(type.GetString(), "http", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!connectionDescriptor.TryGetProperty("url", out var urlProperty)
            || urlProperty.GetString() is not { Length: > 0 } url)
        {
            throw new TransportException("HTTP transport descriptor must include a url.");
        }

        var builder = new UriBuilder(url)
        {
            Scheme = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "transport/connect",
        };

        var socket = new ClientWebSocket();
        if (connectionDescriptor.TryGetProperty("dev-tunnel-token", out var tokenProperty)
            && tokenProperty.GetString() is { Length: > 0 } token)
        {
            socket.Options.SetRequestHeader("X-Tunnel-Authorization", $"tunnel {token}");
        }

        await socket.ConnectAsync(builder.Uri, ct).ConfigureAwait(false);
        return new HttpTransport(socket);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
