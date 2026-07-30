namespace Phantom.Workspaces.Llm;

public interface IRunningSubAgent
{
    string AgentId { get; }
    string DisplayName { get; }
    string Description { get; }

    /// <summary>
    /// Caller-supplied sub-agent name/id (issue #1151). Defaults to empty; concrete
    /// implementations forward it from <c>AgentChat.Name</c>. Distinct from
    /// <see cref="DisplayName"/> (agent-type label).
    /// </summary>
    string Name => string.Empty;

    AgentChatCompletionState CompletionState { get; }
    DateTime LastUpdatedAt { get; }
    IReadOnlyList<IRunningSubAgent> SubAgents { get; }
}
