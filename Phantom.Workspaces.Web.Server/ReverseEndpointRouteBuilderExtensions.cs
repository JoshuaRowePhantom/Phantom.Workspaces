using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Web.Server;

/// <summary>
/// Maps the reverse-execution WebSocket endpoint. A connecting instance opens a WebSocket here,
/// registers a client-instance id, and the server can then push reverse agent-execution requests
/// back over the same duplex connection. See <c>docs/design/reverse-tunnel-trust-execution.md</c>.
/// </summary>
public static class ReverseEndpointRouteBuilderExtensions
{
    /// <summary>Maps <c>GET /reverse/connect</c> (WebSocket upgrade) onto the supplied route builder.</summary>
    /// <param name="endpointRouteBuilder">The route builder.</param>
    /// <param name="registry">The registry connections are registered in (a DI singleton).</param>
    /// <param name="isKnownClientInstance">
    /// Optional validation of the claimed <c>user-computer-profile</c> client-instance id; defaults to
    /// accepting any non-empty id within the tunnel-authenticated channel.
    /// </param>
    public static IEndpointRouteBuilder MapReverseEndpoints(
        this IEndpointRouteBuilder endpointRouteBuilder,
        ReverseExecutionRegistry registry,
        Func<string, bool>? isKnownClientInstance = null)
    {
        ArgumentNullException.ThrowIfNull(endpointRouteBuilder);
        ArgumentNullException.ThrowIfNull(registry);

        endpointRouteBuilder.MapGet("/reverse/connect", async (HttpContext httpContext) =>
        {
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            var channel = new WebSocketReverseMessageChannel(socket, ownsSocket: false);
            var acceptor = new ReverseConnectionAcceptor(registry, isKnownClientInstance);
            await acceptor.AcceptAsync(channel, httpContext.RequestAborted).ConfigureAwait(false);
        });

        return endpointRouteBuilder;
    }
}
