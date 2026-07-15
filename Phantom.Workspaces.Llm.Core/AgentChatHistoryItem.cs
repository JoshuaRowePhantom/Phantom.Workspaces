using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// A single chat history entry (user or completed assistant turn).
/// </summary>
public sealed record AgentChatHistoryItem
{
    public static ChatRole DiagnosticChatRole { get; } = new("diagnostic");
    public static ChatRole HelpChatRole { get; } = new("help");

    public ChatRole Role { get; init; }

    /// <summary>
    /// UTC time at which this item was originally created.
    /// <see langword="null"/> means the timestamp is unknown (e.g. loaded from legacy history that
    /// predates timestamp support).
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// The tool call ID of the parent tool call that spawned a sub-agent, when this item represents
    /// the completion of that tool call. <see langword="null"/> when not applicable or when loaded
    /// from records that predate this property.
    /// </summary>
    public string? ParentToolCallId { get; init; }

    /// <summary>Structured content blocks for this turn.</summary>
    public IReadOnlyList<AIContent> Contents { get; init; } = [];
}