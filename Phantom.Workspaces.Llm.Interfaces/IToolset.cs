using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Interfaces;

public interface IToolset
{
    Task<AITool[]> ListToolsAsync();
}
