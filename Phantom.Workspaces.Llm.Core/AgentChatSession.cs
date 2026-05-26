using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

public sealed class AgentChatSession
{
    public AgentChatSession(ChatClientAgent agent, AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(session);
        this.Agent = agent;
        this.Session = session;
    }

    public ChatClientAgent Agent { get; }

    public AgentSession Session { get; }

    public IAsyncEnumerable<AgentResponseUpdate> RunStreamAsync(
        ChatMessage[] messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return this.Agent.RunStreamingAsync(messages, this.Session, cancellationToken: cancellationToken);
    }
}
