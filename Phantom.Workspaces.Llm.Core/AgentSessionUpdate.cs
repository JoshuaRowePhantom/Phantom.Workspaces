using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

public sealed record AgentSessionUpdate
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    public ChatResponseUpdate? ResponseUpdate { get; init; }
}
