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

    /// <summary>Structured content blocks for this turn.</summary>
    public IReadOnlyList<AIContent> Contents { get; init; } = [];
}
