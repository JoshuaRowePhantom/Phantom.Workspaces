namespace Phantom.Workspaces.Llm.Interfaces;

public readonly record struct AgentSessionId(string Value)
{
    public override string ToString() => Value;
}
