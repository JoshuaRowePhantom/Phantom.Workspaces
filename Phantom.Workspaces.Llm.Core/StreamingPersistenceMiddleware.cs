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
    private string? currentAssistantRunId;
    private AgentDocumentOrigin? documentOrigin;

    internal event Action<ChatMessage>? MessagePersisted;

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

    internal void SetAssistantRunId(string runId, AgentDocumentOrigin? origin = null)
    {
        this.currentAssistantRunId = runId;
        this.documentOrigin = origin;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // #1327: The per-update `ToChatResponse()` coalescing (a full text concatenation + fresh
        // char[] allocation over the whole growing buffer) and the persistence-store I/O are pure
        // compute / I/O. When this middleware is pumped from the Avalonia UI thread (the process
        // loop resumes on the foreground scheduler by design), running that work synchronously on
        // the caller's thread blocks input, rendering and animation for the duration of the stream.
        //
        // Offload each update's coalescing + persistence onto the thread pool via Task.Run so it
        // never runs on the caller's (potentially UI) SynchronizationContext. The enumeration stays
        // strictly one-update-per-consumer-pull (rather than an eager background pump), which both
        // preserves the incremental-persistence cadence and the ordering invariant (each stable
        // message is persisted before its triggering update is yielded to the consumer), and keeps
        // back-pressure identical to the previous synchronous implementation. Only the pure
        // compute / I/O moves off-thread; the consumer's own MoveNextAsync continuation still
        // resumes on whatever context it prefers (in the GUI host, the foreground scheduler), so
        // the binding-affecting view-model mutations downstream remain on the foreground context.
        var buffer = new List<ChatResponseUpdate>();
        var persistedCount = 0;
        var persistenceFailed = false;
        var assistantRunId = this.currentAssistantRunId ?? Guid.NewGuid().ToString("n");
        var session = this.currentSession;
        var origin = this.documentOrigin;
        PersistedAgent? agentSnapshot = null;
        var drain = this.inner.GetService<CopilotSdkChatClient>() is not null ? new CopilotSdkOutputDrain() : null;
        if (drain is not null)
        {
            options = options?.Clone() ?? new();
            options.AdditionalProperties ??= [];
            options.AdditionalProperties[CopilotSdkOutputDrain.OptionName] = drain;
        }
        using var cancellationRegistration = cancellationToken.Register(() => drain?.RequestStop());
        var providerCompleted = false;
        var providerFailed = false;

        var enumerator = this.inner
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                var step = await Task.Run(
                    async () =>
                    {
                        if (!await MoveProviderAsync().ConfigureAwait(false))
                        {
                            providerCompleted = true;
                            return (HasUpdate: false, Update: (ChatResponseUpdate?)null);
                        }

                        var update = enumerator.Current;
                        await AcceptUpdateAsync(update).ConfigureAwait(false);
                        return (HasUpdate: true, Update: (ChatResponseUpdate?)update);
                    },
                    drain is null ? cancellationToken : CancellationToken.None).ConfigureAwait(false);

                if (!step.HasUpdate)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                yield return step.Update!;
            }

        }
        finally
        {
            try
            {
                if (drain is not null && !providerCompleted && !providerFailed && !persistenceFailed)
                {
                    drain.RequestStop();
                    while (await MoveProviderAsync().ConfigureAwait(false))
                        await AcceptUpdateAsync(enumerator.Current).ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    // This buffer and the provider's sealed accepted suffix have the same owner.
                    // An ambiguous failed append must never be retried during cleanup.
                    if (!persistenceFailed && buffer.Count > 0)
                        await Task.Run(async () =>
                        {
                            var finalMessages = buffer.ToChatResponse().Messages;
                            for (var i = persistedCount; i < finalMessages.Count; i++)
                                await PersistNextAsync(finalMessages[i]).ConfigureAwait(false);
                        }, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    await enumerator.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        async ValueTask<bool> MoveProviderAsync()
        {
            try { return await enumerator.MoveNextAsync().ConfigureAwait(false); }
            catch { providerFailed = true; throw; }
        }

        async ValueTask AcceptUpdateAsync(ChatResponseUpdate update)
        {
            if (agentSnapshot is null && session is not null)
            {
                // Native identity is established by the first provider read. Freeze
                // its owning snapshot before a later run can update provider metadata.
                var agent = this.provider.BuildPersistedAgent(session);
                agentSnapshot = agent with
                {
                    AgentSessionJson = agent.AgentSessionJson?.DeepClone().AsBsonDocument,
                    AgentDefinitionJson = agent.AgentDefinitionJson?.DeepClone().AsBsonDocument,
                };
            }
            buffer.Add(update);
            var response = buffer.ToChatResponse();
            var stableCount = update.FinishReason is not null
                ? response.Messages.Count
                : Math.Max(0, response.Messages.Count - 1);
            for (var i = persistedCount; i < stableCount; i++)
                await PersistNextAsync(response.Messages[i]).ConfigureAwait(false);
            if (update.FinishReason is not null)
            {
                // An autonomous batch can precede a new request in one enumeration.
                // Its durable final must not absorb the next task.
                buffer.Clear();
                persistedCount = 0;
            }
        }

        async ValueTask PersistNextAsync(ChatMessage message)
        {
            try
            {
                await this.PersistMessageAsync(message, assistantRunId, agentSnapshot, origin).ConfigureAwait(false);
                persistedCount++;
            }
            catch
            {
                persistenceFailed = true;
                throw;
            }
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

    internal const string AssistantRunIdProperty = "phantomAssistantRunId";

    private async ValueTask PersistMessageAsync(
        ChatMessage message,
        string assistantRunId,
        PersistedAgent? agentSnapshot,
        AgentDocumentOrigin? documentOrigin)
    {
        if (agentSnapshot is not { } agent)
        {
            return;
        }

        if (message.CreatedAt is null)
        {
            message.CreatedAt = DateTimeOffset.UtcNow;
        }

        message.AdditionalProperties ??= [];
        message.AdditionalProperties[AssistantRunIdProperty] = assistantRunId;
        if (documentOrigin is { } origin)
            message.AdditionalProperties[AgentDocumentOrigin.PropertyName] = origin;
        await this.store.StoreAsync(
            new StoreRequestAgent
            {
                Agent = agent,
                NewMessages = [message],
            },
            CancellationToken.None).ConfigureAwait(false);
        this.MessagePersisted?.Invoke(message);
    }
}
