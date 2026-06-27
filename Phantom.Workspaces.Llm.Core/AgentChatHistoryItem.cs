using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// A single chat history entry (user or completed assistant turn).
/// </summary>
public sealed record AgentChatHistoryItem
{
    public static ChatRole DiagnosticChatRole { get; } = new("diagnostic");

    public ChatRole Role { get; init; }

    /// <summary>
    /// UTC time at which this item was originally created.
    /// <see langword="null"/> means the timestamp is unknown (e.g. loaded from legacy history that
    /// predates timestamp support).
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Structured content blocks for this turn.</summary>
    public IReadOnlyList<AIContent> Contents { get; init; } = [];
}