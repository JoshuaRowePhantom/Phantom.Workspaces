using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Reflection;

namespace Phantom.Workspaces.Llm;

internal sealed class AgentFrameworkChatHistoryProvider : ChatHistoryProvider
{
    private static readonly MethodInfo ProvideChatHistoryAsyncMethod = typeof(ChatHistoryProvider).GetMethod(
        "ProvideChatHistoryAsync",
        BindingFlags.Instance | BindingFlags.NonPublic,
        [typeof(ChatHistoryProvider.InvokingContext), typeof(CancellationToken)])
        ?? throw new InvalidOperationException("Unable to locate ChatHistoryProvider.ProvideChatHistoryAsync.");

    private static readonly MethodInfo StoreChatHistoryAsyncMethod = typeof(ChatHistoryProvider).GetMethod(
        "StoreChatHistoryAsync",
        BindingFlags.Instance | BindingFlags.NonPublic,
        [typeof(ChatHistoryProvider.InvokedContext), typeof(CancellationToken)])
        ?? throw new InvalidOperationException("Unable to locate ChatHistoryProvider.StoreChatHistoryAsync.");

    private readonly ChatHistoryProvider configuredProvider;

    public AgentFrameworkChatHistoryProvider(ChatHistoryProvider configuredProvider)
        : base(null, null, null)
    {
        this.configuredProvider = configuredProvider ?? throw new ArgumentNullException(nameof(configuredProvider));
    }

    internal event EventHandler<InvocationStartingEventArgs>? InvocationStarting;

    internal event EventHandler<HistoryStoredEventArgs>? HistoryStored;

    public override IReadOnlyList<string> StateKeys => this.configuredProvider.StateKeys;

    protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        ChatHistoryProvider.InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        this.InvocationStarting?.Invoke(
            this,
            new InvocationStartingEventArgs(context.Session!, context.RequestMessages.ToArray()));

        return InvokeProvideChatHistoryAsync(context, cancellationToken);
    }

    protected override ValueTask StoreChatHistoryAsync(
        ChatHistoryProvider.InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        return StoreAndRaiseAsync(context, cancellationToken);
    }

    private async ValueTask StoreAndRaiseAsync(
        ChatHistoryProvider.InvokedContext context,
        CancellationToken cancellationToken)
    {
        await InvokeStoreChatHistoryAsync(context, cancellationToken);
        this.HistoryStored?.Invoke(
            this,
            new HistoryStoredEventArgs(context.Session!, context.ResponseMessages?.ToArray() ?? Array.Empty<ChatMessage>()));
    }

    private ValueTask<IEnumerable<ChatMessage>> InvokeProvideChatHistoryAsync(
        ChatHistoryProvider.InvokingContext context,
        CancellationToken cancellationToken)
    {
        var result = ProvideChatHistoryAsyncMethod.Invoke(this.configuredProvider, [context, cancellationToken]);
        if (result is ValueTask<IEnumerable<ChatMessage>> typedResult)
        {
            return typedResult;
        }

        throw new InvalidOperationException("Configured ChatHistoryProvider returned an unexpected ProvideChatHistoryAsync result.");
    }

    private ValueTask InvokeStoreChatHistoryAsync(
        ChatHistoryProvider.InvokedContext context,
        CancellationToken cancellationToken)
    {
        var result = StoreChatHistoryAsyncMethod.Invoke(this.configuredProvider, [context, cancellationToken]);
        if (result is ValueTask typedResult)
        {
            return typedResult;
        }

        throw new InvalidOperationException("Configured ChatHistoryProvider returned an unexpected StoreChatHistoryAsync result.");
    }

    internal sealed record InvocationStartingEventArgs(
        AgentSession Session,
        IReadOnlyList<ChatMessage> RequestMessages);

    internal sealed record HistoryStoredEventArgs(
        AgentSession Session,
        IReadOnlyList<ChatMessage> Messages);
}
