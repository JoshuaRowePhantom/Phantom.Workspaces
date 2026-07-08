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

    /// <summary>
    /// Persists a parent→child session ID link. Idempotent — calling twice with the same pair is safe.
    /// </summary>
    ValueTask AddSubAgentLinkAsync(
        string parentSessionId,
        string childSessionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all child session IDs that were registered under <paramref name="parentSessionId"/>.
    /// Returns an empty list when no children are known.
    /// </summary>
    ValueTask<IReadOnlyList<AgentSessionId>> ReadSubAgentChildIdsAsync(
        string parentSessionId,
        CancellationToken cancellationToken = default);
}

public readonly record struct PersistedAgent
{
    public required string AgentSessionId { get; init; }

    public BsonDocument? AgentSessionJson { get; init; }

    public BsonDocument? AgentDefinitionJson { get; init; }

    /// <summary>
    /// The GitHub Copilot SDK session identifier, when the agent uses the <c>github-copilot</c>
    /// provider. Persisting it lets the Copilot CLI session be resumed (with its conversation
    /// history) after a restart instead of starting fresh (issue #3).
    /// </summary>
    public string? CopilotSdkSessionId { get; init; }
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


