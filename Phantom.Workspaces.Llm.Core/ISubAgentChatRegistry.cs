using AgentSchema;

namespace Phantom.Workspaces.Llm;

public interface ISubAgentChatRegistry
{
    Task<ISubAgentChat> GetOrCreateAsync(
        string agentId,
        AgentDefinition subAgentDefinition,
        string parentToolCallId,
        CancellationToken cancellationToken = default);

    ISubAgentChat? TryGet(string agentId);
}
