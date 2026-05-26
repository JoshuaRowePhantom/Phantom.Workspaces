using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

internal sealed class AgentFrameworkChatHistoryProvider : ChatHistoryProvider
{
    private const string SessionStateKey = nameof(AgentFrameworkChatHistoryProvider);

    public AgentFrameworkChatHistoryProvider()
        : base(null, null, null)
    {
    }

    internal event EventHandler<InvocationStartingEventArgs>? InvocationStarting;

    internal event EventHandler<HistoryStoredEventArgs>? HistoryStored;

    public override IReadOnlyList<string> StateKeys => [SessionStateKey];

    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        ChatHistoryProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        this.InvocationStarting?.Invoke(
            this,
            new InvocationStartingEventArgs(context.Session, context.RequestMessages.ToArray()));

        if (context.Session.TryGetInMemoryChatHistory(out var messages, SessionStateKey) && messages is not null)
        {
            return ValueTask.FromResult<IEnumerable<ChatMessage>>(messages);
        }

        return ValueTask.FromResult<IEnumerable<ChatMessage>>([]);
    }

    protected override ValueTask StoreChatHistoryAsync(
        ChatHistoryProvider.InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        var messages = context.Session.TryGetInMemoryChatHistory(out var existingMessages, SessionStateKey) && existingMessages is not null
            ? existingMessages
            : [];

        messages.AddRange(context.RequestMessages);
        if (context.ResponseMessages is not null)
        {
            messages.AddRange(context.ResponseMessages);
        }

        context.Session.SetInMemoryChatHistory(messages, SessionStateKey);
        this.HistoryStored?.Invoke(
            this,
            new HistoryStoredEventArgs(context.Session, context.ResponseMessages?.ToArray() ?? Array.Empty<ChatMessage>()));
        return ValueTask.CompletedTask;
    }

    internal sealed record InvocationStartingEventArgs(
        AgentSession Session,
        IReadOnlyList<ChatMessage> RequestMessages);

    internal sealed record HistoryStoredEventArgs(
        AgentSession Session,
        IReadOnlyList<ChatMessage> Messages);
}
