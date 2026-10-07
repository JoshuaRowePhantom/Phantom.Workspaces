namespace Phantom.Workspaces.Llm.Remote;

/// <summary>A child identity advertised by a remote owner; authorization still requires open-subagent.</summary>
public interface IRemoteSubagentReference : IRunningSubAgent
{
    string AgentSessionId { get; }
}
