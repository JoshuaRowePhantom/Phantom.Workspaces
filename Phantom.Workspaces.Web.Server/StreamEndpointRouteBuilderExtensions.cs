using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Phantom.Workspaces.Llm.Shell;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Web.Server;

/// <summary>
/// Maps the stream-open WebSocket endpoint. A client opens a WebSocket here, sends the
/// <see cref="TrustedStreamRequest"/> as a JSON text message, then exchanges binary stream frames
/// (framed with the 5-byte <see cref="StreamFramedMessageChannel"/> encoding) for the stream
/// lifetime. This is the server side of <c>WebRemoteStreamClient</c>.
/// </summary>
public static class StreamEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Maps <c>GET /stream/open</c> (WebSocket upgrade) onto the supplied route builder.</summary>
    public static IEndpointRouteBuilder MapStreamEndpoints(this IEndpointRouteBuilder endpointRouteBuilder)
    {
        ArgumentNullException.ThrowIfNull(endpointRouteBuilder);

        endpointRouteBuilder.MapGet("/stream/open", async (HttpContext httpContext) =>
        {
            if (!httpContext.WebSockets.IsWebSocketRequest)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var cancellationToken = httpContext.RequestAborted;
            using var socket = await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

            // Read the initial JSON text message containing the TrustedStreamRequest.
            TrustedStreamRequest? request = null;
            var initialBuffer = new byte[65536];
            using var initialMessage = new System.IO.MemoryStream();

            while (true)
            {
                System.Net.WebSockets.WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(initialBuffer, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close
                    || result.MessageType != System.Net.WebSockets.WebSocketMessageType.Text)
                {
                    return;
                }

                initialMessage.Write(initialBuffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            request = JsonSerializer.Deserialize<TrustedStreamRequest>(initialMessage.ToArray(), SerializerOptions);
            if (request is null)
            {
                return;
            }

            var localExecutor = new LocalTrustedExecutor();
            await using var channel = new WebSocketStreamMessageChannel(socket, ownsSocket: false);
            try
            {
                await localExecutor.HandleStreamAsync(
                    request.StreamKind, request.OpenPayload, channel, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Stream handler error; close the WebSocket cleanly.
            }
        });

        return endpointRouteBuilder;
    }
}
