using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Llm;

/// <summary>Tracks one dispatched sub-agent for the lifetime of the dispatcher session.</summary>
internal sealed class DispatchedSubAgent
{
    public required string Id { get; init; }

    public required string Description { get; init; }

    /// <summary>Caches the embedding of <see cref="Description"/> for fuzzy routing.</summary>
    public required IReadOnlyList<float> DescriptionEmbedding { get; init; }

    public required EntityId EntityId { get; init; }

    public required RunningAgentChatLease Lease { get; init; }

    /// <summary>
    /// Set to <see cref="DateTimeOffset.UtcNow"/> each time the sub-agent becomes idle after a
    /// dispatch. Drives the recency bias in fuzzy routing.
    /// </summary>
    public DateTimeOffset LastUpdated { get; set; }

    /// <summary>
    /// Index into AgentChat.History at the time the last dispatch was sent.
    /// Used to capture only newly added ChatMessage items after the sub-agent becomes idle.
    /// </summary>
    public int DispatchHistoryIndex { get; set; }
}
