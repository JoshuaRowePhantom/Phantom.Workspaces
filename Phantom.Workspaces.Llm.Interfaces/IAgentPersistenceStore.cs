using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;

namespace Phantom.Workspaces.Llm.Interfaces;

public interface IAgentPersistenceStore
{
    ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default);

    ValueTask<PersistedAgent?> RestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ChatMessage[]> ReadMessagesAsync(
        ReadMessagesRequest request,
        CancellationToken cancellationToken = default);
}

public readonly record struct PersistedAgent
{
    public required string AgentSessionId { get; init; }

    public BsonDocument? AgentSessionJson { get; init; }

    public BsonDocument? AgentDefinitionJson { get; init; }
}

public readonly record struct StoreRequestAgent
{
    public required PersistedAgent Agent { get; init; }

    public ChatMessage[]? NewMessages { get; init; }
}

public readonly record struct RestoreRequest
{
    public required string AgentSessionId { get; init; }
}

public readonly record struct ReadMessagesRequest
{
    public required string AgentSessionId { get; init; }
}
