using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Http;
using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Web.Server;

/// <summary>
/// Hosts the reverse hub on the transport model. Maps a websocket endpoint backed by a
/// <see cref="ReverseHttpServerTransportFactory"/> (hosted as a <see cref="TransportRegistry"/>
/// listener): a <c>reverse-register</c> channel-open stores the executor's registration channel
/// (and updates the <see cref="ReverseConnectionStatusRegistry"/>), and a <c>reverse-http</c>
/// relay channel-open runs a byte-transparent relay pump between a forwarding client and a
/// registered executor's registration channel; an unknown entity-id yields
/// <c>channel-open-error {"error-code":"not-registered"}</c>, which the forwarding factory surfaces
/// as a <see cref="TransportException"/>. Registered alongside the existing <c>/reverse</c> endpoints
/// (not yet replacing them).
/// </summary>
public static class TransportReverseEndpointRouteBuilderExtensions
{
    /// <summary>The websocket endpoint path clients open to register with, or relay through, the hub.</summary>
    public const string TransportReverseEndpointPath = "/reverse-transport/connect";

    /// <summary>
    /// Maps the transport reverse-relay websocket endpoint, hosting <paramref name="serverTransportFactory"/>
    /// (which feeds <paramref name="statusRegistry"/>) as the only <see cref="TransportRegistry"/> listener.
    /// </summary>
    public static IEndpointRouteBuilder MapTransportReverseEndpoints(
        this IEndpointRouteBuilder endpointRouteBuilder,
        ReverseHttpServerTransportFactory serverTransportFactory,
        ReverseConnectionStatusRegistry statusRegistry)
    {
        ArgumentNullException.ThrowIfNull(endpointRouteBuilder);
        ArgumentNullException.ThrowIfNull(serverTransportFactory);
        ArgumentNullException.ThrowIfNull(statusRegistry);

        var registry = new TransportRegistry();
        registry.Register(serverTransportFactory);
        var activeTransports = new ConcurrentDictionary<ServerHttpTransport, byte>();

        endpointRouteBuilder.MapGet(TransportReverseEndpointPath, async (HttpContext httpContext) =>
        {
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var transport = new ServerHttpTransport(socket, registry);
            activeTransports.TryAdd(transport, 0);
            try
            {
                await transport.RunAsync(httpContext.RequestAborted).ConfigureAwait(false);
            }
            finally
            {
                activeTransports.TryRemove(transport, out _);
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        });

        return endpointRouteBuilder;
    }
}
