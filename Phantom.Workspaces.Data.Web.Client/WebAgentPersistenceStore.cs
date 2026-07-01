using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Data.Web.Client;

public sealed class WebAgentPersistenceStore : IAgentPersistenceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = AIJsonUtilities.DefaultOptions;

    private sealed record AgentSessionIdRequest(string AgentSessionId);

    private readonly HttpClient httpClient;

    public WebAgentPersistenceStore(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
    }

    public async ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
    {
        var dto = new StoreAgentRequest
        {
            Agent = ToDto(request.Agent),
            NewMessages = request.NewMessages,
        };

        using var response = await this.httpClient
            .PostAsJsonAsync("/agent/persistence/store", dto, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async ValueTask<PersistedAgent?> RestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await this.httpClient
            .PostAsJsonAsync(
                "/agent/persistence/restore",
                new AgentSessionIdRequest(request.AgentSessionId),
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var dto = await response.Content
            .ReadFromJsonAsync<PersistedAgentDto>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        return dto is null ? null : FromDto(dto);
    }

    public async ValueTask<ChatMessage[]> ReadMessagesAsync(
        ReadMessagesRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await this.httpClient
            .PostAsJsonAsync(
                "/agent/persistence/messages",
                new AgentSessionIdRequest(request.AgentSessionId),
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var result = await response.Content
            .ReadFromJsonAsync<ReadMessagesResponse>(SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        return result?.Messages ?? [];
    }

    private static PersistedAgentDto ToDto(PersistedAgent agent) => new()
    {
        AgentSessionId = agent.AgentSessionId,
        AgentSessionJson = agent.AgentSessionJson is null
            ? null
            : JsonDocument.Parse(agent.AgentSessionJson.ToJson()).RootElement,
        AgentDefinitionJson = agent.AgentDefinitionJson is null
            ? null
            : JsonDocument.Parse(agent.AgentDefinitionJson.ToJson()).RootElement,
        CopilotSdkSessionId = agent.CopilotSdkSessionId,
    };

    private static PersistedAgent FromDto(PersistedAgentDto dto) => new()
    {
        AgentSessionId = dto.AgentSessionId,
        AgentSessionJson = dto.AgentSessionJson is null
            ? null
            : BsonDocument.Parse(dto.AgentSessionJson.Value.GetRawText()),
        AgentDefinitionJson = dto.AgentDefinitionJson is null
            ? null
            : BsonDocument.Parse(dto.AgentDefinitionJson.Value.GetRawText()),
        CopilotSdkSessionId = dto.CopilotSdkSessionId,
    };
}
