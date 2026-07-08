namespace Phantom.Workspaces.Llm.Interfaces;

/// <summary>
/// Marker interface for the running agent chat factory, accessible from
/// <see cref="AgentServices"/> without introducing a circular project dependency.
/// Cast to <c>Phantom.Workspaces.Llm.IRunningAgentChatFactory</c> to access the full API.
/// </summary>
public interface IRunningAgentChatFactory { }
