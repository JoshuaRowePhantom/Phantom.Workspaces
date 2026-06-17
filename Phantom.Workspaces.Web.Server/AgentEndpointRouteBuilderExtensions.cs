using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Web.Server;

/// <summary>
/// Maps the remote agent execution endpoint used by the Workspaces trust-model remoting client.
/// </summary>
public static class AgentEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions = AIJsonUtilities.DefaultOptions;

    /// <summary>Maps <c>POST /agent/respond</c> onto the supplied route builder.</summary>
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

            var reverseExecutionRegistry = httpContext.RequestServices.GetService(typeof(ReverseExecutionRegistry))
                as ReverseExecutionRegistry;
            var response = await AgentRespondHandler
                .RespondAsync(request, reverseExecutionRegistry, cancellationToken)
                .ConfigureAwait(false);
            return Results.Json(response, SerializerOptions);
        });

        return endpointRouteBuilder;
    }
}
