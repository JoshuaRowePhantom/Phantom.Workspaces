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
    internal event Action<ChatResponseUpdate>? AcceptedUpdateRecovered;
    internal StreamingAggregationWork? AggregationWork { get; private set; }

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
        if (options?.AdditionalProperties?.GetValueOrDefault(CopilotSdkOutputDrain.CancellationOptionName) is CancellationToken owningCancellation)
            cancellationToken = owningCancellation;
        var admittedBatch = options?.AdditionalProperties?.GetValueOrDefault(CopilotSdkRootBatch.OptionName) as CopilotSdkRootBatch;
        if (admittedBatch?.HasAcceptedOutput != true) cancellationToken.ThrowIfCancellationRequested();
        // Keep pure aggregation and store I/O off the foreground, with one pull's back-pressure.
        // The cursor visits each newly stable message once; only authoritative identity
        // corrections materialize a previously persisted message again.
        var accumulator = new StreamingResponseAccumulator(TimeProvider.System);
        var persistedCount = 0;
        var persistenceFailed = false;
        var persistedById = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
        var assistantRunId = this.currentAssistantRunId ?? Guid.NewGuid().ToString("n");
        var session = this.currentSession;
        var origin = this.documentOrigin;
        PersistedAgent? agentSnapshot = null;
        var drain = this.inner.GetService<CopilotSdkChatClient>() is not null ? new CopilotSdkOutputDrain() : null;
        if (drain is not null)
        {
            if (admittedBatch?.HasAcceptedOutput == true)
                drain.Attach(admittedBatch.Owner.SealAccepted);
            options = options?.Clone() ?? new();
            options.AdditionalProperties ??= [];
            options.AdditionalProperties[CopilotSdkOutputDrain.OptionName] = drain;
        }
        using var cancellationRegistration = cancellationToken.Register(() => drain?.RequestStop());
        var providerCompleted = false;
        var providerFailed = false;
        Exception? primaryFailure = null;

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
                        try
                        {
                            if (drain is null) cancellationToken.ThrowIfCancellationRequested();
                            if (!await MoveProviderAsync().ConfigureAwait(false))
                            {
                                providerCompleted = true;
                                return (HasUpdate: false, Update: (ChatResponseUpdate?)null);
                            }
                            var update = enumerator.Current;
                            await AcceptUpdateAsync(update).ConfigureAwait(false);
                            return (HasUpdate: true, Update: (ChatResponseUpdate?)update);
                        }
                        catch (Exception failure) { primaryFailure ??= failure; throw; }
                    },
                    CancellationToken.None).ConfigureAwait(false);

                if (!step.HasUpdate)
                {
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    this.AcceptedUpdateRecovered?.Invoke(step.Update!);
                    primaryFailure = new OperationCanceledException(cancellationToken);
                    throw primaryFailure;
                }
                yield return step.Update!;
            }

        }
        finally
        {
            var cleanupFailures = new List<Exception>();
            await CaptureCleanupAsync(async () =>
            {
                if (drain is not null && !providerCompleted && !providerFailed && !persistenceFailed)
                {
                    drain.RequestStop();
                    await Task.Run(async () =>
                    {
                        while (await MoveProviderAsync().ConfigureAwait(false))
                        {
                            await AcceptUpdateAsync(enumerator.Current).ConfigureAwait(false);
                            this.AcceptedUpdateRecovered?.Invoke(enumerator.Current);
                        }
                    }, CancellationToken.None).ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
            await CaptureCleanupAsync(async () =>
            {
                // This buffer and the provider's sealed accepted suffix have the same owner.
                // An ambiguous failed append must never be retried during cleanup.
                if (!persistenceFailed && accumulator.Count > 0)
                    await Task.Run(async () =>
                    {
                        while (persistedCount < accumulator.Count)
                            await PersistNextAsync(accumulator.GetMessage(persistedCount)).ConfigureAwait(false);
                    }, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
            await CaptureCleanupAsync(async () => await enumerator.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
            this.AggregationWork = accumulator.Measure();
            if (cleanupFailures.Count > 0)
            {
                var failures = new List<Exception>();
                if (primaryFailure is not null) failures.Add(primaryFailure);
                failures.AddRange(cleanupFailures.Where(error => !ReferenceEquals(error, primaryFailure)));
                if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
                throw new AggregateException("Provider invocation and accepted-output cleanup failed.", failures);
            }

            async Task CaptureCleanupAsync(Func<Task> cleanup)
            {
                try { await cleanup().ConfigureAwait(false); }
                catch (Exception failure) { cleanupFailures.Add(failure); }
            }
        }

        async ValueTask<bool> MoveProviderAsync()
        {
            try { return await enumerator.MoveNextAsync().ConfigureAwait(false); }
            catch (Exception failure) { providerFailed = true; primaryFailure ??= failure; throw; }
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
            accumulator.Add(update);
            foreach (var index in accumulator.TakeChangedIndices())
            {
                if (index >= persistedCount) continue;
                var corrected = accumulator.GetMessage(index);
                if (corrected.MessageId is { } id && persistedById.TryGetValue(id, out var previous))
                    corrected.CreatedAt = previous.CreatedAt;
                await PersistNextAsync(corrected, replacement: true).ConfigureAwait(false);
            }
            var stableCount = update.FinishReason is not null
                ? accumulator.Count
                : Math.Max(0, accumulator.Count - 1);
            for (var i = persistedCount; i < stableCount; i++)
                await PersistNextAsync(accumulator.GetMessage(i)).ConfigureAwait(false);
        }

        async ValueTask PersistNextAsync(ChatMessage message, bool replacement = false)
        {
            try
            {
                if (replacement)
                {
                    message.AdditionalProperties ??= [];
                    message.AdditionalProperties[StoreRequestAgent.ReplacementPropertyName] = true;
                }
                await this.PersistMessageAsync(message, assistantRunId, agentSnapshot, origin).ConfigureAwait(false);
                if (!replacement) persistedCount++;
                if (message.MessageId is { } id) persistedById[id] = message;
            }
            catch (Exception failure)
            {
                persistenceFailed = true;
                primaryFailure ??= failure;
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

    internal const string AssistantRunIdProperty = StoreRequestAgent.AssistantRunIdPropertyName;

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
