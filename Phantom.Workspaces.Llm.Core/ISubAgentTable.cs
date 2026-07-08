namespace Phantom.Workspaces.Llm;

public interface ISubAgentTable
{
    /// <summary>
    /// Registers a newly created sub-agent with this parent.
    /// Adds it to the parent's SubAgents observable collection,
    /// persists the parent→child link, and returns the SubAgent wrapper.
    /// </summary>
    SubAgent Add(AgentChat agentChat);
}
