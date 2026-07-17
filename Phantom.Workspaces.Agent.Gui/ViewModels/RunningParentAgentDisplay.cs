using System;
using System.Collections.Generic;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>
/// UI-layer display wrapper for a sub-agent's parent chat. Reuses <see cref="RunningSubAgentDisplay"/>
/// to observe the parent's recent activity, but exposes the parent's session id as
/// <see cref="AgentId"/> (so the rendered link navigates to the parent view) and no nested
/// sub-agents (the parent panel shows only the parent's own activity).
/// </summary>
public sealed class RunningParentAgentDisplay : IRunningSubAgentDisplay, IDisposable
{
    private readonly RunningSubAgentDisplay inner;
    private readonly string agentId;

    public RunningParentAgentDisplay(AgentChat parentAgentChat)
    {
        ArgumentNullException.ThrowIfNull(parentAgentChat);
        this.inner = RunningSubAgentDisplay.CreateActivityOnly(parentAgentChat);
        this.agentId = parentAgentChat.AgentSessionId;
    }

    public string AgentId => this.agentId;
    public string DisplayName => this.inner.DisplayName;
    public string Description => this.inner.Description;
    public AgentChatCompletionState CompletionState => this.inner.CompletionState;
    public IReadOnlyList<SubAgentActivityLine> RecentActivity => this.inner.RecentActivity;
    public IReadOnlyList<IRunningSubAgentDisplay> SubAgents => [];

    public event EventHandler? ActivityChanged
    {
        add => this.inner.ActivityChanged += value;
        remove => this.inner.ActivityChanged -= value;
    }

    public event EventHandler? CompletionStateChanged
    {
        add => this.inner.CompletionStateChanged += value;
        remove => this.inner.CompletionStateChanged -= value;
    }

    public void Dispose() => this.inner.Dispose();
}
