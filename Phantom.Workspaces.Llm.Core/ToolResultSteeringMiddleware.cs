using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// An <see cref="IChatClient"/> middleware that injects pending steering input at tool-result
/// boundaries. At each model call where the last message contains
/// <see cref="FunctionResultContent"/>, any non-held items available in the
/// <see cref="AgentInputQueueManager"/> are dequeued and appended to the message list before
/// forwarding to the inner client. No buffer is maintained — items come directly from the queue
/// manager (see <c>docs/design/steerable-chat-implementation.md</c>).
/// </summary>
internal sealed class ToolResultSteeringMiddleware : IChatClient
{
    private readonly IChatClient inner;
    private readonly AgentInputQueueManager queueManager;

    public ToolResultSteeringMiddleware(IChatClient inner, AgentInputQueueManager queueManager)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(queueManager);
        this.inner = inner;
        this.queueManager = queueManager;
    }

    /// <summary>
    /// Raised after steering messages are injected into the model call so the owning
    /// <c>AgentChat</c> can record them in its visible chat history.
    /// </summary>
    internal event Action<IReadOnlyList<ChatMessage>>? MessagesInjected;

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && serviceType == typeof(ToolResultSteeringMiddleware))
        {
            return this;
        }

        return this.inner.GetService(serviceType, serviceKey);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await this.inner
            .GetResponseAsync(this.InjectQueuedIfToolResult(messages), options, cancellationToken)
            .ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in this.inner
            .GetStreamingResponseAsync(this.InjectQueuedIfToolResult(messages), options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    public void Dispose() => this.inner.Dispose();

    // If the last message carries FunctionResultContent, drain Immediate queue items and
    // append them as additional messages before the model call. Only Immediate-immediacy items
    // are injected at tool boundaries; Queue-immediacy items wait until the end of the current
    // turn. TryDequeueNextImmediate excludes both Held and Queue-immediacy items and is
    // CAS-based, so concurrent drains are safe.
    private IList<ChatMessage> InjectQueuedIfToolResult(IEnumerable<ChatMessage> messages)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        if (messageList.Count == 0
            || !messageList[^1].Contents.OfType<FunctionResultContent>().Any())
        {
            return messageList;
        }

        List<ChatMessage>? augmented = null;
        List<ChatMessage>? injected = null;
        while (this.queueManager.TryDequeueNextImmediate(out var item))
        {
            augmented ??= [.. messageList];
            foreach (var message in item.Messages ?? [])
            {
                augmented.Add(message);
                (injected ??= []).Add(message);
            }
        }

        if (injected is not null)
        {
            this.MessagesInjected?.Invoke(injected);
        }

        return augmented ?? messageList;
    }
}
