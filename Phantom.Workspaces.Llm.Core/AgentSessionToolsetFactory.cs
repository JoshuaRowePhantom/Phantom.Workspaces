using Microsoft.Agents.AI;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Toolset factory for the <c>agent-session</c> tool kind. Creates an
/// <see cref="AgentSessionToolset"/> that exposes the nine <c>agent_session_*</c> tools
/// allowing a parent agent to create, send messages to, monitor, and stop subagent sessions.
/// </summary>
internal sealed class AgentSessionToolsetFactory : IToolsetFactory
{
    private readonly AgentChatRef _parentChatRef;
    private readonly CurrentSessionContext _currentSessionContext;
    private readonly IRunningAgentChatFactory _factory;
    private readonly IToolsetFactory? _underlyingToolsetFactory;

    internal AgentSessionToolsetFactory(
        AgentChatRef parentChatRef,
        CurrentSessionContext currentSessionContext,
        IRunningAgentChatFactory factory,
        IToolsetFactory? underlyingToolsetFactory = null)
    {
        _parentChatRef = parentChatRef;
        _currentSessionContext = currentSessionContext;
        _factory = factory;
        _underlyingToolsetFactory = underlyingToolsetFactory;
    }

    public Task<AIContextProvider?> CreateToolsetAsync(AgentSchema.Tool tool, AgentServices agentServices)
    {
        if (string.Equals(tool.Kind, "agent-session", StringComparison.Ordinal))
        {
            return Task.FromResult<AIContextProvider?>(
                new AgentSessionToolset(_parentChatRef, _currentSessionContext, _factory));
        }

        if (_underlyingToolsetFactory is not null)
            return _underlyingToolsetFactory.CreateToolsetAsync(tool, agentServices);

        return Task.FromResult<AIContextProvider?>(null);
    }
}
