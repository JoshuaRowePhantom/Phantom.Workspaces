using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Agents.AI;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

public sealed record AgentServices
{
    public bool LogChat { get; init; }

    public bool LogHttpRequests { get; init; }

    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>
    /// Overrides the agent persistence store used by the agent, bypassing the store
    /// configured in the agent definition. Intended for testing.
    /// </summary>
    public IAgentPersistenceStore? AgentPersistenceStoreOverride { get; init; }

    /// <summary>
    /// Overrides the chat client used by the agent. Intended for deterministic tests.
    /// </summary>
    public IChatClient? ChatClientOverride { get; init; }

    /// <summary>
    /// Overrides toolset factory resolution for custom tool kinds.
    /// </summary>
    public IToolsetFactory? ToolsetFactory { get; init; }

    /// <summary>
    /// Factory used to resolve the tool resources referenced by an agent manifest into concrete
    /// tools. Used when a <see cref="CreateAgentChatRequest"/> supplies an agent manifest without
    /// its own tool resource factory.
    /// </summary>
    public IToolResourceFactory? ToolResourceFactory { get; init; }
}
