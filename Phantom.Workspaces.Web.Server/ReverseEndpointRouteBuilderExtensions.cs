using System;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Web.Server;

/// <summary>
/// Maps the reverse-execution duplex endpoints. A connecting instance opens either a WebSocket
/// (<c>GET /reverse/connect</c>) or a streaming HTTP connection (<c>POST /reverse/connect-http</c>)
/// here, registers a client-instance id, and the server can then push reverse agent-execution
/// requests back over the same duplex connection. See
/// <c>docs/design/reverse-tunnel-trust-execution.md</c>.
/// </summary>
public static class ReverseEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>GET /reverse/connect</c> (WebSocket upgrade) and
    /// <c>POST /reverse/connect-http</c> (NDJSON streaming) onto the supplied route builder.
    /// </summary>
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

        endpointRouteBuilder.MapPost("/reverse/connect-http", async (HttpContext httpContext) =>
        {
            httpContext.Response.ContentType = "application/x-ndjson";
            await httpContext.Response.StartAsync(httpContext.RequestAborted).ConfigureAwait(false);

            // Flush the response body writer before reading the request body. This ensures the
            // client-side SendAsync resolves (which depends on the first response-body flush in
            // in-memory test transports) before the server starts consuming the request stream,
            // enabling concurrent bidirectional streaming over both HTTP/1.1 and HTTP/2.
            await httpContext.Response.BodyWriter.FlushAsync(httpContext.RequestAborted).ConfigureAwait(false);

            var channel = new HttpReverseMessageChannel(
                httpContext.Request.BodyReader,
                httpContext.Response.BodyWriter);
            var acceptor = new ReverseConnectionAcceptor(registry, isKnownClientInstance);
            await acceptor.AcceptAsync(channel, httpContext.RequestAborted).ConfigureAwait(false);
        });

        return endpointRouteBuilder;
    }
}
