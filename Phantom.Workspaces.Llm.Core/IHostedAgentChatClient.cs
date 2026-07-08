namespace Phantom.Workspaces.Llm;

/// <summary>
/// Marker for IChatClient implementations that do not accept direct user messages.
/// AgentChat.AcceptsUserInput returns false when its chat client implements this interface.
/// </summary>
public interface IHostedAgentChatClient { }
