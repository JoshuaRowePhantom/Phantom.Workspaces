namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed record SubAgentActivityLine(SubAgentActivityKind Kind, string Text);
public enum SubAgentActivityKind { ToolCall, AgentText, SubAgent }
