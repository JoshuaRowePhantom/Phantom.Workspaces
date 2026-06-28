using System.Collections.ObjectModel;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

internal sealed class AgentChatHistoryService
{
    private readonly AgentChatHistoryCollection history;
    private readonly AgentFrameworkChatHistoryProvider configuredProvider;
    private AgentSession? activeSession;
    private AgentFrameworkChatHistoryProvider? provider;

    public AgentChatHistoryService(
        AgentChatHistoryCollection history,
        AgentFrameworkChatHistoryProvider chatHistoryProvider)
    {
        this.history = history;
        this.configuredProvider = chatHistoryProvider ?? throw new ArgumentNullException(nameof(chatHistoryProvider));
    }

    public bool IsProviderBound => this.provider is not null;

    public void BindSession(AgentChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var nextProvider = this.configuredProvider;
        if (!ReferenceEquals(this.provider, nextProvider))
        {
            this.provider = nextProvider;
        }

        this.activeSession = session.Session;
    }

    public void BeginInvocation(ChatMessage[] requestMessages)
    {
        if (requestMessages.Length == 0)
        {
            return;
        }

        foreach (var message in requestMessages)
        {
            if (message.Role != ChatRole.User)
            {
                continue;
            }

            var contents = message.Contents.ToArray();
            var nextItem = new AgentChatHistoryItem
            {
                Role = ChatRole.User,
                Contents = contents,
                Timestamp = DateTimeOffset.UtcNow,
            };

            this.history.Add(nextItem);
        }
    }

    // Invocation and response history are applied by AgentChat's run loop to keep UI ordering stable.
}
