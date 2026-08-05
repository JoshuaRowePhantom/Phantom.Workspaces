using System.Collections.Generic;

namespace Phantom.Workspaces.Llm.SlashCommands;

/// <summary>A lightweight description of a currently active sub-agent, for slash-command completions.</summary>
public sealed record SubAgentDescriptor(string Id, string Description);

/// <summary>
/// The surface a <see cref="SubAgentDispatcherChatClient"/> exposes to its slash-command handlers.
/// Handlers use it to enumerate available sub-agent definitions and currently active sub-agents for
/// listing and completions; command execution enqueues the equivalent routing message onto the
/// dispatcher's own <see cref="AgentChat"/>.
/// </summary>
public interface ISubAgentDispatcherCommandClient
{
    /// <summary>The sub-agent templates available to the dispatcher (the <c>agent-definition</c> tools).</summary>
    IReadOnlyList<AgentDefinitionTool> AvailableDefinitions { get; }

    /// <summary>The sub-agents that have been dispatched during this session.</summary>
    IReadOnlyList<SubAgentDescriptor> ActiveSubAgents { get; }
}
