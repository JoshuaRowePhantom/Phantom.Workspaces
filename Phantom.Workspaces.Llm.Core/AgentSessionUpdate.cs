namespace Phantom.Workspaces.Llm;

public sealed record AgentSessionUpdate
{
    public required LlmSession LlmSession { get; init; }

    public LlmStreamEvent? LlmStreamingEvent { get; init; }
}
