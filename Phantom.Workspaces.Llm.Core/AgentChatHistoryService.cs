using System.Collections.ObjectModel;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

internal sealed class AgentChatHistoryService
{
    private readonly ObservableCollection<AgentChatHistoryItem> history;
    private AgentSession? activeSession;
    private AgentFrameworkChatHistoryProvider? provider;

    public AgentChatHistoryService(ObservableCollection<AgentChatHistoryItem> history)
    {
        this.history = history;
    }

    public bool IsProviderBound => this.provider is not null;

    public void BindSession(AgentChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var nextProvider = session.Agent.ChatHistoryProvider as AgentFrameworkChatHistoryProvider;
        if (!ReferenceEquals(this.provider, nextProvider))
        {
            if (this.provider is not null)
            {
                this.provider.InvocationStarting -= this.OnInvocationStarting;
                this.provider.HistoryStored -= this.OnHistoryStored;
            }

            this.provider = nextProvider;
            if (this.provider is not null)
            {
                this.provider.InvocationStarting += this.OnInvocationStarting;
                this.provider.HistoryStored += this.OnHistoryStored;
            }
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
            this.history.Add(new AgentChatHistoryItem
            {
                Role = ChatRole.User,
                Contents = contents,
            });
        }
    }

    private AgentChatHistoryItem? CommitFromMessages(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            var contents = message.Contents.ToArray();
            this.history.Add(new AgentChatHistoryItem
            {
                Role = message.Role,
                Contents = contents,
                IsInProgress = false,
            });
        }

        return this.history.LastOrDefault(static item => item.Role == ChatRole.Assistant);
    }

    private void OnInvocationStarting(object? sender, AgentFrameworkChatHistoryProvider.InvocationStartingEventArgs e)
    {
        if (!ReferenceEquals(e.Session, this.activeSession))
        {
            return;
        }

        this.BeginInvocation(e.RequestMessages.ToArray());
    }

    private void OnHistoryStored(object? sender, AgentFrameworkChatHistoryProvider.HistoryStoredEventArgs e)
    {
        if (!ReferenceEquals(e.Session, this.activeSession))
        {
            return;
        }

        if (e.Messages.Count > 0)
        {
            this.CommitFromMessages(e.Messages);
        }
    }
}
