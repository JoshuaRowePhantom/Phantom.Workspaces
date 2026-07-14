namespace Phantom.Workspaces.Llm;

public interface IRunningSubAgent
{
    string AgentId { get; }
    string DisplayName { get; }
    string Description { get; }
    AgentChatCompletionState CompletionState { get; }
    DateTime LastUpdatedAt { get; }
    IReadOnlyList<IRunningSubAgent> SubAgents { get; }
}
