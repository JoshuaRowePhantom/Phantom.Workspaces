using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Interfaces;

public interface IToolsetFactory
{
    Task<IToolset> CreateToolsetAsync(
        string name,
        Dictionary<string, object> properties,
        AgentServices agentServices);
}
