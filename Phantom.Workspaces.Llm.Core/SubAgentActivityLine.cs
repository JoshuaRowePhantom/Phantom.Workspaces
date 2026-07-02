namespace Phantom.Workspaces.Llm;

public sealed record SubAgentActivityLine(SubAgentActivityKind Kind, string Text);
public enum SubAgentActivityKind { ToolCall, AgentText, SubAgent }
