namespace Phantom.Workspaces.Llm;

public interface IRunningSubAgent
{
    string AgentId { get; }
    string DisplayName { get; }
    AgentChatCompletionState CompletionState { get; }
    DateTime LastUpdatedAt { get; }
    IReadOnlyList<SubAgentActivityLine> RecentActivity { get; }
    IReadOnlyList<IRunningSubAgent> SubAgents { get; }
    event EventHandler? ActivityChanged;
}
