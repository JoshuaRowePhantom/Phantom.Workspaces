using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Web.Server;

/// <summary>
/// Maps the remote agent execution endpoint used by the Workspaces trust-model remoting client.
/// </summary>
public static class AgentEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions = AIJsonUtilities.DefaultOptions;

    // AIJsonUtilities.DefaultOptions uses WriteIndented = true; NDJSON requires one object per line.
    private static readonly JsonSerializerOptions NdjsonOptions =
        new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    /// <summary>Maps <c>POST /agent/respond</c> and <c>POST /agent/chat/{sessionId}/turn</c> onto the supplied route builder.</summary>
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder endpointRouteBuilder)
    {
        ArgumentNullException.ThrowIfNull(endpointRouteBuilder);

        endpointRouteBuilder.MapPost("/agent/respond", async (HttpContext httpContext) =>
        {
            var cancellationToken = httpContext.RequestAborted;
            var request = await JsonSerializer
                .DeserializeAsync<RemoteAgentRequest>(httpContext.Request.Body, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (request is null)
            {
                return Results.BadRequest("Empty remote agent request.");
            }

            var response = await AgentRespondHandler
                .RespondAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(response, SerializerOptions);
        });

        endpointRouteBuilder.MapPost("/agent/chat/{sessionId}/turn", async (HttpContext httpContext, string sessionId) =>
        {
            var cancellationToken = httpContext.RequestAborted;
            var request = await JsonSerializer
                .DeserializeAsync<AgentChatTurnRequest>(httpContext.Request.Body, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (request is null)
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsync("Empty agent chat turn request.", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var cache = httpContext.RequestServices.GetRequiredService<AgentChatSessionCache>();

            httpContext.Response.ContentType = "application/x-ndjson";
            await foreach (var update in cache.RunTurnAsync(request, cancellationToken).ConfigureAwait(false))
            {
                var line = JsonSerializer.Serialize(update, NdjsonOptions);
                await httpContext.Response.WriteAsync(line + "\n", cancellationToken).ConfigureAwait(false);
                await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        });

        return endpointRouteBuilder;
    }
}