using System;
using System.Collections.Generic;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>
/// UI-layer view of a running sub-agent. All events fire on the foreground/UI thread.
/// </summary>
public interface IRunningSubAgentDisplay
{
    string AgentId { get; }
    string DisplayName { get; }
    string Description { get; }

    /// <summary>
    /// Caller-supplied sub-agent name/id (issue #1151). Defaults to empty; concrete adapters
    /// forward it from <c>AgentChat.Name</c>. Distinct from <see cref="DisplayName"/>.
    /// </summary>
    string Name => string.Empty;

    AgentChatCompletionState CompletionState { get; }
    IReadOnlyList<SubAgentActivityLine> RecentActivity { get; }
    IReadOnlyList<IRunningSubAgentDisplay> SubAgents { get; }
    event EventHandler? ActivityChanged;
    event EventHandler? CompletionStateChanged;
}
