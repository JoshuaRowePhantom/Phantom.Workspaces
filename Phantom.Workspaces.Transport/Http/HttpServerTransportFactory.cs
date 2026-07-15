using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Phantom.Workspaces.Transport.Http;

public sealed class HttpServerTransportFactory : IAsyncDisposable
{
    public const string EndpointPath = "/transport/connect";

    private readonly TransportRegistry registry;
    private readonly TransportOptions options;
    private readonly ConcurrentDictionary<ServerHttpTransport, byte> activeTransports = new();

    public HttpServerTransportFactory(TransportRegistry registry, IOptions<TransportOptions>? options = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.options = options?.Value ?? new TransportOptions();
    }

    public ITransportRegistry Registry => this.registry;

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(EndpointPath, this.AcceptAsync);
    }

    private async Task AcceptAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var transport = new ServerHttpTransport(socket, this.registry, this.options.ServerLeaseDuration);
        this.activeTransports.TryAdd(transport, 0);
        try
        {
            await transport.RunAsync(context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            this.activeTransports.TryRemove(transport, out _);
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var transport in this.activeTransports.Keys)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }

        this.activeTransports.Clear();
    }
}
