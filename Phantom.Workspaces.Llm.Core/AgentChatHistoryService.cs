using System.Collections.ObjectModel;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

internal sealed class AgentChatHistoryService
{
    private readonly ObservableCollection<AgentChatHistoryItem> history;
    private readonly AgentFrameworkChatHistoryProvider configuredProvider;
    private AgentSession? activeSession;
    private AgentFrameworkChatHistoryProvider? provider;

    public AgentChatHistoryService(
        ObservableCollection<AgentChatHistoryItem> history,
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
            };

            if (this.history.Count > 0 && AreEquivalent(this.history[^1], nextItem))
            {
                continue;
            }

            this.history.Add(nextItem);
        }
    }

    private AgentChatHistoryItem? CommitFromMessages(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            var contents = message.Contents.ToArray();
            var nextItem = new AgentChatHistoryItem
            {
                Role = message.Role,
                Contents = contents,
                IsInProgress = false,
            };

            if (this.history.Count > 0 && AreEquivalent(this.history[^1], nextItem))
            {
                continue;
            }

            this.history.Add(nextItem);
        }

        return this.history.LastOrDefault(static item => item.Role == ChatRole.Assistant);
    }

    private static bool AreEquivalent(AgentChatHistoryItem left, AgentChatHistoryItem right)
    {
        return left.Role == right.Role
            && left.Text == right.Text
            && left.ReasoningText == right.ReasoningText
            && left.IsInProgress == right.IsInProgress;
    }

    // Invocation and response history are applied by AgentChat's run loop to keep UI ordering stable.
}
