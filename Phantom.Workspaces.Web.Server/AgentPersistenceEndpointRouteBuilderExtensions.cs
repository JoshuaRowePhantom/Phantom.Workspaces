using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data.Web.Client;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Web.Server;

/// <summary>
/// Maps the agent persistence endpoints used by remote hosts to persist agent session state
/// and chat history back to the authoritative server-side store.
/// </summary>
public static class AgentPersistenceEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions SerializerOptions = AIJsonUtilities.DefaultOptions;

    private sealed record AgentSessionIdRequest(string AgentSessionId);

    /// <summary>
    /// Maps <c>POST /agent/persistence/store</c>, <c>POST /agent/persistence/restore</c>,
    /// <c>POST /agent/persistence/messages</c>, <c>POST /agent/persistence/sub-agent-links/add</c>,
    /// and <c>POST /agent/persistence/sub-agent-links/read</c> onto the supplied route builder.
    /// </summary>
    public static IEndpointRouteBuilder MapAgentPersistenceEndpoints(
        this IEndpointRouteBuilder endpointRouteBuilder)
    {
        ArgumentNullException.ThrowIfNull(endpointRouteBuilder);

        endpointRouteBuilder.MapPost("/agent/persistence/store", async (HttpContext httpContext) =>
        {
            var cancellationToken = httpContext.RequestAborted;

            var store = httpContext.RequestServices.GetService(typeof(IAgentPersistenceStore))
                as IAgentPersistenceStore;

            if (store is null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            StoreAgentRequest? request;
            try
            {
                request = await JsonSerializer
                    .DeserializeAsync<StoreAgentRequest>(httpContext.Request.Body, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return Results.BadRequest("Malformed store request.");
            }

            if (request is null)
            {
                return Results.BadRequest("Empty store request.");
            }

            var domainAgent = FromDto(request.Agent);
            await store.StoreAsync(
                new StoreRequestAgent
                {
                    Agent = domainAgent,
                    NewMessages = request.NewMessages,
                },
                cancellationToken).ConfigureAwait(false);

            return Results.Ok();
        });

        endpointRouteBuilder.MapPost("/agent/persistence/restore", async (HttpContext httpContext) =>
        {
            var cancellationToken = httpContext.RequestAborted;

            var store = httpContext.RequestServices.GetService(typeof(IAgentPersistenceStore))
                as IAgentPersistenceStore;

            if (store is null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var request = await JsonSerializer
                .DeserializeAsync<AgentSessionIdRequest>(httpContext.Request.Body, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (request is null)
            {
                return Results.BadRequest("Empty restore request.");
            }

            var agent = await store.RestoreAsync(
                new RestoreRequest { AgentSessionId = request.AgentSessionId },
                cancellationToken).ConfigureAwait(false);

            if (agent is null)
            {
                return Results.NotFound();
            }

            return Results.Json(ToDto(agent.Value), SerializerOptions);
        });

        endpointRouteBuilder.MapPost("/agent/persistence/messages", async (HttpContext httpContext) =>
        {
            var cancellationToken = httpContext.RequestAborted;

            var store = httpContext.RequestServices.GetService(typeof(IAgentPersistenceStore))
                as IAgentPersistenceStore;

            if (store is null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var request = await JsonSerializer
                .DeserializeAsync<AgentSessionIdRequest>(httpContext.Request.Body, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (request is null)
            {
                return Results.BadRequest("Empty messages request.");
            }

            var messages = await store.ReadMessagesAsync(
                new ReadMessagesRequest { AgentSessionId = request.AgentSessionId },
                cancellationToken).ConfigureAwait(false);

            return Results.Json(new ReadMessagesResponse { Messages = messages }, SerializerOptions);
        });

        endpointRouteBuilder.MapPost("/agent/persistence/sub-agent-links/add", async (HttpContext httpContext) =>
        {
            var cancellationToken = httpContext.RequestAborted;

            var store = httpContext.RequestServices.GetService(typeof(IAgentPersistenceStore))
                as IAgentPersistenceStore;

            if (store is null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var request = await JsonSerializer
                .DeserializeAsync<AddSubAgentLinkRequest>(httpContext.Request.Body, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (request is null)
            {
                return Results.BadRequest("Empty add sub-agent link request.");
            }

            await store.AddSubAgentLinkAsync(
                request.ParentSessionId,
                request.ChildSessionId,
                cancellationToken).ConfigureAwait(false);

            return Results.Ok();
        });

        endpointRouteBuilder.MapPost("/agent/persistence/sub-agent-links/read", async (HttpContext httpContext) =>
        {
            var cancellationToken = httpContext.RequestAborted;

            var store = httpContext.RequestServices.GetService(typeof(IAgentPersistenceStore))
                as IAgentPersistenceStore;

            if (store is null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var request = await JsonSerializer
                .DeserializeAsync<ReadSubAgentChildIdsRequest>(httpContext.Request.Body, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            if (request is null)
            {
                return Results.BadRequest("Empty read sub-agent child IDs request.");
            }

            var childIds = await store.ReadSubAgentChildIdsAsync(request.ParentSessionId, cancellationToken)
                .ConfigureAwait(false);

            return Results.Json(
                new ReadSubAgentChildIdsResponse { ChildSessionIds = childIds.Select(static id => id.Value).ToArray() },
                SerializerOptions);
        });

        return endpointRouteBuilder;
    }

    private static PersistedAgentDto ToDto(PersistedAgent agent) => new()
    {
        AgentSessionId = agent.AgentSessionId,
        AgentSessionJson = agent.AgentSessionJson.ToJsonElement(),
        AgentDefinitionJson = agent.AgentDefinitionJson.ToJsonElement(),
        CopilotSdkSessionId = agent.CopilotSdkSessionId,
        LastUpdatedUtc = agent.LastUpdatedUtc,
    };

    private static PersistedAgent FromDto(PersistedAgentDto dto) => new()
    {
        AgentSessionId = dto.AgentSessionId,
        AgentSessionJson = dto.AgentSessionJson.ToBsonDocument(),
        AgentDefinitionJson = dto.AgentDefinitionJson.ToBsonDocument(),
        CopilotSdkSessionId = dto.CopilotSdkSessionId,
        LastUpdatedUtc = dto.LastUpdatedUtc,
    };
}
