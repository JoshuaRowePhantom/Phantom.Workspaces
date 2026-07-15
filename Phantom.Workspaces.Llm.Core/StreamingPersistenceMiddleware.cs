using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;
using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// An <see cref="IChatClient"/> middleware that incrementally persists streaming response messages
/// as soon as they stabilise (i.e. as soon as the next message starts arriving), instead of waiting
/// for the entire LLM sub-call to complete. This means a process crash mid-stream loses at most the
/// currently-streaming message, not the entire in-flight response.
/// <para>
/// The middleware is the sole writer for response messages. The corresponding
/// <see cref="IncrementalPersistenceChatHistoryProvider.StoreChatHistoryAsync"/> is a no-op, so
/// there is no double-writing or deduplication bookkeeping required.
/// </para>
/// </summary>
internal sealed class StreamingPersistenceMiddleware : IChatClient
{
    private readonly IChatClient inner;
    private readonly IncrementalPersistenceChatHistoryProvider provider;
    private readonly IAgentPersistenceStore store;
    private AgentSession? currentSession;

    public StreamingPersistenceMiddleware(
        IChatClient inner,
        IncrementalPersistenceChatHistoryProvider provider,
        IAgentPersistenceStore store)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Sets the current <see cref="AgentSession"/> so that store writes can include the correct
    /// session metadata. Must be called before each streaming invocation (wired via
    /// <see cref="AgentFrameworkChatHistoryProvider.InvocationStarting"/>).
    /// </summary>
    public void SetCurrentSession(AgentSession session)
    {
        this.currentSession = session;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new List<ChatResponseUpdate>();
        var persistedCount = 0;

        await foreach (var update in this.inner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            buffer.Add(update);

            // Persist all stable messages (0..stableCount-1) before yielding this update so that
            // when the consumer receives each update, the preceding stable messages are already
            // durable. The last active message (stableCount..N-1) is not yet stable — it may
            // still receive more tokens. When FinishReason is set the entire response is final.
            var response = buffer.ToChatResponse();
            var stableCount = update.FinishReason is not null
                ? response.Messages.Count
                : Math.Max(0, response.Messages.Count - 1);

            for (var i = persistedCount; i < stableCount; i++)
            {
                await this.PersistMessageAsync(response.Messages[i]).ConfigureAwait(false);
            }

            persistedCount = stableCount;

            yield return update;
        }
    }

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => this.inner.GetResponseAsync(messages, options, cancellationToken);

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is null && serviceType == typeof(StreamingPersistenceMiddleware))
        {
            return this;
        }

        return this.inner.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    public void Dispose() => this.inner.Dispose();

    private async ValueTask PersistMessageAsync(ChatMessage message)
    {
        var session = this.currentSession;
        if (session is null)
        {
            return;
        }

        if (message.CreatedAt is null)
        {
            message.CreatedAt = DateTimeOffset.UtcNow;
        }

        var agent = this.provider.BuildPersistedAgent(session);
        await this.store.StoreAsync(
            new StoreRequestAgent
            {
                Agent = agent,
                NewMessages = [message],
            },
            CancellationToken.None).ConfigureAwait(false);
    }
}
