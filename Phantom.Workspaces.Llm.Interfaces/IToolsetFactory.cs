using Microsoft.Agents.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Interfaces;

public interface IToolsetFactory
{
    Task<AIContextProvider?> CreateToolsetAsync(
        AgentSchema.Tool tool,
        AgentServices agentServices);
}
