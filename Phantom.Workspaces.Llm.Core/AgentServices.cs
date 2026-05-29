using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

public sealed class AgentServices
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
}
