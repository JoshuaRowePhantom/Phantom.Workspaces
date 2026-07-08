namespace Phantom.Workspaces.Llm;

/// <summary>
/// A late-bound reference to an <see cref="AgentChat"/> used when the toolset factory
/// must be wired before the parent <see cref="AgentChat"/> has finished construction.
/// The <see cref="Chat"/> property is set by <see cref="AgentFactory.CreateAgentChatAsync"/>
/// immediately after <see cref="AgentChat.CreateAsync"/> returns.
/// </summary>
internal sealed class AgentChatRef
{
    public AgentChat? Chat { get; set; }

    public AgentChatRef() { }

    public AgentChatRef(AgentChat chat)
    {
        Chat = chat;
    }
}
