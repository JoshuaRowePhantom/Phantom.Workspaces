using AgentSchema;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

public struct CreateAgentChatRequest
{
    public string? AgentSessionId { get; init; }

    public AgentDefinition? AgentDefinition { get; init; }

    public AgentServices? AgentServices { get; init; }
}
