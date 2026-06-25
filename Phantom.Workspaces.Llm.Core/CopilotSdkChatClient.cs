using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// An <see cref="IChatClient"/> adapter that bridges the GitHub Copilot SDK
/// (<see cref="CopilotClient"/> / <see cref="CopilotSession"/>) into the
/// Microsoft Agent Framework chat-client pipeline used throughout
/// <c>Phantom.Workspaces.Llm.Core</c>.
/// </summary>
/// <remarks>
/// The Copilot SDK manages a stateful CLI session that retains conversation
/// context across turns. This adapter therefore creates a single persistent
/// session on first use and, for each request, forwards only the latest user
/// message to that session. Prior turns are remembered by the Copilot session
/// itself, while the agent framework continues to record history for
/// presentation and persistence. When a stored SDK session id is supplied via
/// <see cref="SetResumeSessionId"/>, the first session is resumed rather than
/// created, so history survives a process restart (issue #3).
/// </remarks>
public sealed class CopilotSdkChatClient : IChatClient, IAsyncDisposable, ISelfInvokingToolChatClient
{
    private readonly string modelId;
    private readonly string displayName;
    private readonly string? gitHubToken;
    private readonly ILoggerFactory? loggerFactory;
    private readonly CopilotByokOptions? byokOptions;
    private readonly string? cliPath;
    private readonly AgentInputQueueManager? queueManager;
    private readonly SemaphoreSlim sessionInitializationLock = new(1, 1);
    private readonly SemaphoreSlim turnLock = new(1, 1);

    private CopilotClient? copilotClient;
    private CopilotSession? copilotSession;
    private string? currentSessionSignature;
    private string? pendingResumeSessionId;
    private int disposeStarted;

    /// <summary>
    /// Raised when a steering message is forwarded to the live Copilot session so the owning
    /// <c>AgentChat</c> can record it in its visible chat history.
    /// </summary>
    internal event Action<ChatMessage>? SteeringMessageForwarded;

    /// <summary>
    /// Raised after a Copilot SDK session is created or resumed, carrying its
    /// <see cref="CopilotSession.SessionId"/> so the owning <c>AgentChat</c> can persist it for
    /// later resumption (issue #3).
    /// </summary>
    internal event Action<string>? SessionEstablished;

    /// <summary>
    /// Creates a new <see cref="CopilotSdkChatClient"/>.
    /// </summary>
    /// <param name="modelId">The Copilot model identifier (for example, <c>gpt-5</c>).</param>
    /// <param name="displayName">A human-readable display name for the client.</param>
    /// <param name="gitHubToken">
    /// The GitHub token used to authenticate. When <see langword="null"/>, the SDK uses the
    /// logged-in Copilot user instead.
    /// </param>
    /// <param name="loggerFactory">Optional logger factory for SDK diagnostics.</param>
    /// <param name="byokOptions">
    /// Optional bring-your-own-key configuration pointing the session at a custom
    /// OpenAI-compatible endpoint instead of GitHub's hosted models.
    /// </param>
    /// <param name="cliPath">Optional explicit path to the Copilot CLI executable.</param>
    /// <param name="queueManager">
    /// Optional input-queue manager. When supplied, items added to non-held queues during a streaming
    /// turn are forwarded to the live Copilot session as immediate steering input.
    /// </param>
    public CopilotSdkChatClient(
        string modelId,
        string displayName,
        string? gitHubToken,
        ILoggerFactory? loggerFactory,
        CopilotByokOptions? byokOptions = null,
        string? cliPath = null,
        AgentInputQueueManager? queueManager = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        this.modelId = modelId;
        this.displayName = displayName;
        this.gitHubToken = string.IsNullOrWhiteSpace(gitHubToken) ? null : gitHubToken;
        this.loggerFactory = loggerFactory;
        this.byokOptions = byokOptions;
        this.cliPath = string.IsNullOrWhiteSpace(cliPath) ? null : cliPath;
        this.queueManager = queueManager;
    }

    /// <summary>
    /// Builds the Copilot SDK <see cref="ProviderConfig"/> from BYOK options for the given model.
    /// </summary>
    public static ProviderConfig CreateProviderConfig(CopilotByokOptions byokOptions, string modelId)
    {
        ArgumentNullException.ThrowIfNull(byokOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var providerConfig = new ProviderConfig
        {
            Type = byokOptions.ProviderType,
            WireApi = byokOptions.WireApi,
            BaseUrl = byokOptions.BaseUrl,
            ApiKey = byokOptions.ApiKey,
            BearerToken = byokOptions.BearerToken,
            ModelId = modelId,
            WireModel = byokOptions.WireModel,
        };

        if (byokOptions.Headers is not null)
        {
            providerConfig.Headers = new Dictionary<string, string>(byokOptions.Headers);
        }

        return providerConfig;
    }

    /// <summary>
    /// Builds the Copilot SDK <see cref="SessionConfig"/> for a turn, forwarding the agent's
    /// model, BYOK provider, reasoning effort, system instructions, and—critically—its function
    /// tools. The Copilot CLI otherwise only exposes its own built-in tools, so without forwarding
    /// <see cref="ChatOptions.Tools"/> the workspace <see cref="AIFunction"/>s (for example
    /// <c>workspaces_entity_get</c>) never reach the model.
    /// </summary>
    public static SessionConfig BuildSessionConfig(
        string modelId,
        CopilotByokOptions? byokOptions,
        ChatOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var sessionConfig = new SessionConfig
        {
            Model = modelId,
            Streaming = true,
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        if (byokOptions is not null)
        {
            sessionConfig.Provider = CreateProviderConfig(byokOptions, modelId);
        }

        var reasoningEffort = MapReasoningEffort(options?.Reasoning?.Effort);
        if (reasoningEffort is not null)
        {
            sessionConfig.ReasoningEffort = reasoningEffort;
        }

        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            sessionConfig.SystemMessage = new SystemMessageConfig { Content = options.Instructions };
        }

        var tools = options?.Tools?.OfType<AIFunction>().ToList();
        if (tools is { Count: > 0 })
        {
            sessionConfig.Tools = tools;
        }

        return sessionConfig;
    }

    /// <summary>
    /// Builds the Copilot SDK <see cref="ResumeSessionConfig"/> for resuming a previously created
    /// session. Mirrors <see cref="BuildSessionConfig"/> so a resumed session is configured with the
    /// same model, BYOK provider, reasoning effort, system instructions, and function tools (issue #3).
    /// </summary>
    public static ResumeSessionConfig BuildResumeSessionConfig(
        string modelId,
        CopilotByokOptions? byokOptions,
        ChatOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var resumeConfig = new ResumeSessionConfig
        {
            Model = modelId,
            Streaming = true,
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        if (byokOptions is not null)
        {
            resumeConfig.Provider = CreateProviderConfig(byokOptions, modelId);
        }

        var reasoningEffort = MapReasoningEffort(options?.Reasoning?.Effort);
        if (reasoningEffort is not null)
        {
            resumeConfig.ReasoningEffort = reasoningEffort;
        }

        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            resumeConfig.SystemMessage = new SystemMessageConfig { Content = options.Instructions };
        }

        var tools = options?.Tools?.OfType<AIFunction>().ToList();
        if (tools is { Count: > 0 })
        {
            resumeConfig.Tools = tools;
        }

        return resumeConfig;
    }

    /// <summary>
    /// Sets the Copilot SDK session id to resume on the next (first) session creation, so the CLI
    /// session and its conversation history survive a restart (issue #3). The id is consumed once:
    /// later session recreations (for example after a tool-set change) always create a fresh session.
    /// </summary>
    internal void SetResumeSessionId(string? sessionId)
    {
        this.pendingResumeSessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var messageOptions = BuildMessageOptions(messages);
        var session = await this.EnsureSessionAsync(options, cancellationToken).ConfigureAwait(false);

        await this.turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var toolEventLock = new object();
            var toolCalls = new List<FunctionCallContent>();
            var toolResults = new List<FunctionResultContent>();

            using var subscription = session.On(sessionEvent =>
            {
                switch (sessionEvent)
                {
                    case ToolExecutionStartEvent toolStart:
                        lock (toolEventLock)
                        {
                            toolCalls.Add(CopilotToolEventMapper.MapToolStart(toolStart));
                        }

                        break;
                    case ToolExecutionCompleteEvent toolComplete:
                        lock (toolEventLock)
                        {
                            toolResults.Add(CopilotToolEventMapper.MapToolComplete(toolComplete));
                        }

                        break;
                }
            });

            var finalEvent = await session.SendAndWaitAsync(
                messageOptions,
                timeout: null,
                cancellationToken).ConfigureAwait(false);

            lock (toolEventLock)
            {
                return BuildResponse(
                    finalEvent ?? throw new InvalidOperationException("GitHub Copilot session returned no assistant message."),
                    toolCalls,
                    toolResults);
            }
        }
        finally
        {
            this.turnLock.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var messageOptions = BuildMessageOptions(messages);

        await foreach (var update in this.RunStreamingTurnAsync(BeginTurnAsync, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }

        // Establishing the turn (resolving the session, creating the channel, and subscribing to
        // session events) happens inside the turn lock because RunStreamingTurnAsync invokes this
        // delegate while the lock is held. That ordering guarantees the previous turn has fully torn
        // down first, so its events cannot bleed into this turn's channel and its session cannot be
        // invalidated out from under us.
        async Task<StreamingTurnContext> BeginTurnAsync(CancellationToken turnCancellationToken)
        {
            var session = await this.EnsureSessionAsync(options, turnCancellationToken).ConfigureAwait(false);

            var channel = Channel.CreateUnbounded<ChatResponseUpdate>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });

            var eventSubscription = session.On(sessionEvent =>
            {
                switch (sessionEvent)
                {
                    case AssistantMessageDeltaEvent delta when !string.IsNullOrEmpty(delta.Data.DeltaContent):
                        channel.Writer.TryWrite(new ChatResponseUpdate(ChatRole.Assistant, delta.Data.DeltaContent));
                        break;
                    case AssistantReasoningDeltaEvent reasoningDelta when !string.IsNullOrEmpty(reasoningDelta.Data.DeltaContent):
                        channel.Writer.TryWrite(new ChatResponseUpdate
                        {
                            Role = ChatRole.Assistant,
                            Contents = [new TextReasoningContent(reasoningDelta.Data.DeltaContent)],
                        });
                        break;
                    case ToolExecutionStartEvent toolStart:
                        channel.Writer.TryWrite(new ChatResponseUpdate
                        {
                            Role = ChatRole.Assistant,
                            Contents = [CopilotToolEventMapper.MapToolStart(toolStart)],
                        });
                        break;
                    case ToolExecutionCompleteEvent toolComplete:
                        channel.Writer.TryWrite(new ChatResponseUpdate
                        {
                            Role = ChatRole.Tool,
                            Contents = [CopilotToolEventMapper.MapToolComplete(toolComplete)],
                        });
                        break;
                    case SessionErrorEvent error:
                        channel.Writer.TryComplete(new InvalidOperationException(
                            $"GitHub Copilot session error: {error.Data.Message}"));
                        break;
                    case SessionIdleEvent:
                        channel.Writer.TryComplete();
                        break;
                }
            });

            // While a turn is running, forward any non-held queue items as immediate steering input.
            // SendAsync with Mode="immediate" is safe to call concurrently with a live turn.
            void OnQueueChanged(object? sender, AgentInputQueueManager.QueueStateChangedEventArgs e)
            {
                if (e.ChangeKind != AgentInputQueueManager.QueueStateChangeKind.ItemAdded)
                {
                    return;
                }

                while (this.queueManager!.TryDequeueNextImmediateOrQueued(out var item))
                {
                    foreach (var message in item.Messages ?? [])
                    {
                        var text = string.Concat(
                            message.Contents.OfType<TextContent>().Select(content => content.Text));
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            // Record the forwarded steering message in history before sending it.
                            this.SteeringMessageForwarded?.Invoke(message);

                            // Fire-and-forget: Mode="immediate" writes to the CLI's stdin pipe and
                            // returns promptly. Errors are non-fatal for steering.
                            _ = session.SendAsync(
                                new MessageOptions { Prompt = text, Mode = "immediate" },
                                CancellationToken.None);
                        }
                    }
                }
            }

            if (this.queueManager is not null)
            {
                this.queueManager.QueueStateChanged += OnQueueChanged;
            }

            var subscription = new DelegateDisposable(() =>
            {
                eventSubscription.Dispose();
                if (this.queueManager is not null)
                {
                    this.queueManager.QueueStateChanged -= OnQueueChanged;
                }
            });

            return new StreamingTurnContext(
                channel.Reader,
                subscription,
                sendCancellationToken => session.SendAsync(
                    messageOptions,
                    sendCancellationToken),
                () => this.AbortAndInvalidateSessionAsync(session));
        }
    }

    /// <summary>
    /// Runs a single streaming turn under the per-client turn lock. The turn is established (session
    /// resolved, event subscription created) only after the lock is held, and on cancellation the
    /// context's <see cref="StreamingTurnContext.OnCancelledAsync"/> runs before the lock is released
    /// so the in-flight Copilot turn is actually aborted rather than merely abandoned. Acquiring the
    /// lock and creating the subscription inside the guarded scope also ensures a failure while
    /// subscribing can never leak the lock, which would otherwise deadlock every subsequent turn.
    /// </summary>
    internal async IAsyncEnumerable<ChatResponseUpdate> RunStreamingTurnAsync(
        Func<CancellationToken, Task<StreamingTurnContext>> beginTurnAsync,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(beginTurnAsync);

        await this.turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var turn = await beginTurnAsync(cancellationToken).ConfigureAwait(false);
            using (turn.Subscription)
            {
                try
                {
                    await turn.SendAsync(cancellationToken).ConfigureAwait(false);

                    await foreach (var update in turn.Reader
                        .ReadAllAsync(cancellationToken)
                        .ConfigureAwait(false))
                    {
                        yield return update;
                    }
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        await turn.OnCancelledAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            this.turnLock.Release();
        }
    }

    // Actually stops the in-flight Copilot CLI turn. Cancelling the read loop alone leaves the CLI
    // generating and lets a stale SessionIdleEvent from the abandoned turn complete the next turn's
    // channel (a silent empty response), so the session is also invalidated and recreated next turn.
    private async Task AbortAndInvalidateSessionAsync(CopilotSession session)
    {
        try
        {
            await session.AbortAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The turn is being torn down; a failed abort must not mask the user's cancellation.
            this.loggerFactory?.CreateLogger<CopilotSdkChatClient>()
                .LogDebug(exception, "Aborting the interrupted Copilot turn failed; invalidating the session.");
        }

        this.InvalidateCopilotSession(session);
    }

    // Drops the cached session (disposing it in the background) so the next turn creates a fresh one.
    private void InvalidateCopilotSession(CopilotSession session)
    {
        if (Interlocked.CompareExchange(ref this.copilotSession, null, session) != session)
        {
            return;
        }

        this.currentSessionSignature = null;
        _ = Task.Run(async () =>
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                this.loggerFactory?.CreateLogger<CopilotSdkChatClient>()
                    .LogDebug(exception, "Disposing the invalidated Copilot session failed.");
            }
        });
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is null && serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Synchronous disposal of an underlying async CLI process is not safe;
        // callers should prefer DisposeAsync. Trigger background cleanup only.
        if (Interlocked.CompareExchange(ref this.disposeStarted, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(this.DisposeCoreAsync);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref this.disposeStarted, 1, 0) != 0)
        {
            return;
        }

        await this.DisposeCoreAsync().ConfigureAwait(false);
    }

    private async Task DisposeCoreAsync()
    {
        var session = this.copilotSession;
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        var client = this.copilotClient;
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        this.sessionInitializationLock.Dispose();
        this.turnLock.Dispose();
    }

    /// <summary>
    /// Builds a <see cref="MessageOptions"/> from the message history by locating the last user
    /// message and extracting its text prompt and any inline image/data attachments.
    /// </summary>
    internal static MessageOptions BuildMessageOptions(IEnumerable<ChatMessage> messages)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        for (var index = materialized.Count - 1; index >= 0; index--)
        {
            var message = materialized[index];
            if (message.Role != ChatRole.User)
            {
                continue;
            }

            var text = message.Text;
            var dataItems = message.Contents.OfType<DataContent>().ToList();

            if (!string.IsNullOrEmpty(text) || dataItems.Count > 0)
            {
                var options = new MessageOptions { Prompt = text ?? string.Empty };

                if (dataItems.Count > 0)
                {
                    options.Attachments = dataItems
                        .Select(static d => (UserMessageAttachment)new UserMessageAttachmentBlob
                        {
                            Data = Convert.ToBase64String(d.Data.ToArray()),
                            MimeType = d.MediaType ?? string.Empty,
                            DisplayName = d.MediaType ?? "attachment",
                        })
                        .ToList();
                }

                return options;
            }
        }

        var lastWithText = materialized.LastOrDefault(message => !string.IsNullOrEmpty(message.Text));
        return new MessageOptions { Prompt = lastWithText?.Text ?? string.Empty };
    }

    private static ChatResponse BuildResponse(
        AssistantMessageEvent finalEvent,
        IReadOnlyList<FunctionCallContent> toolCalls,
        IReadOnlyList<FunctionResultContent> toolResults)
    {
        var messages = new List<ChatMessage>();

        // Surface tool use the same way other providers do: function calls in an assistant message,
        // their results in a tool message, before the final assistant text.
        if (toolCalls.Count > 0)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, toolCalls.Cast<AIContent>().ToList()));
        }

        if (toolResults.Count > 0)
        {
            messages.Add(new ChatMessage(ChatRole.Tool, toolResults.Cast<AIContent>().ToList()));
        }

        var finalContents = new List<AIContent>();
        if (!string.IsNullOrEmpty(finalEvent.Data.ReasoningText))
        {
            finalContents.Add(new TextReasoningContent(finalEvent.Data.ReasoningText));
        }

        finalContents.Add(new TextContent(finalEvent.Data.Content ?? string.Empty));
        messages.Add(new ChatMessage(ChatRole.Assistant, finalContents));

        return new ChatResponse(messages)
        {
            ResponseId = finalEvent.Data.MessageId,
            ModelId = null,
        };
    }

    private async Task<CopilotSession> EnsureSessionAsync(ChatOptions? options, CancellationToken cancellationToken)
    {
        var signature = ComputeSessionSignature(options);

        if (this.copilotSession is { } existingSession && this.currentSessionSignature == signature)
        {
            return existingSession;
        }

        await this.sessionInitializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.copilotSession is { } alreadyInitialized && this.currentSessionSignature == signature)
            {
                return alreadyInitialized;
            }

            // The Copilot CLI captures the tool set (and other session config) when the session is
            // created, so a change to the effective tools/instructions/reasoning between turns only
            // takes effect by recreating the session. The CLI client is reused across recreations.
            if (this.copilotClient is null)
            {
                var client = new CopilotClient(new CopilotClientOptions
                {
                    GitHubToken = this.gitHubToken,
                    Logger = this.loggerFactory?.CreateLogger<CopilotClient>(),
                    CliPath = this.cliPath,
                });

                await client.StartAsync(cancellationToken).ConfigureAwait(false);
                this.copilotClient = client;
            }

            if (this.copilotSession is { } staleSession)
            {
                await staleSession.DisposeAsync().ConfigureAwait(false);
                this.copilotSession = null;
                this.currentSessionSignature = null;
            }

            var session = await this.CreateOrResumeSessionAsync(options, cancellationToken).ConfigureAwait(false);

            this.copilotSession = session;
            this.currentSessionSignature = signature;
            this.SessionEstablished?.Invoke(session.SessionId);
            return session;
        }
        finally
        {
            this.sessionInitializationLock.Release();
        }
    }

    // Resumes the persisted Copilot session on the first creation when a resume id was provided, so
    // prior conversation history remains visible to the model (issue #3); otherwise creates a fresh
    // session. The resume id is one-shot. If resuming fails (for example the on-disk session has been
    // removed), fall back to creating a new session so the chat stays usable.
    private async Task<CopilotSession> CreateOrResumeSessionAsync(ChatOptions? options, CancellationToken cancellationToken)
    {
        var resumeSessionId = this.pendingResumeSessionId;
        this.pendingResumeSessionId = null;

        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            try
            {
                var resumeConfig = BuildResumeSessionConfig(this.modelId, this.byokOptions, options);
                return await this.copilotClient!
                    .ResumeSessionAsync(resumeSessionId, resumeConfig, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                this.loggerFactory?.CreateLogger<CopilotSdkChatClient>().LogWarning(
                    exception,
                    "Failed to resume Copilot session {SessionId}; creating a new session.",
                    resumeSessionId);
            }
        }

        var sessionConfig = BuildSessionConfig(this.modelId, this.byokOptions, options);
        return await this.copilotClient!.CreateSessionAsync(sessionConfig, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Computes a signature for the session-config inputs that, when changed between turns, require
    /// recreating the Copilot session (so live tool toggling takes effect). The model and BYOK
    /// provider are fixed for the lifetime of the client and are therefore not included. Tool order
    /// is ignored so that only an actual change to the tool set forces a recreation.
    /// </summary>
    public static string ComputeSessionSignature(ChatOptions? options)
    {
        var toolNames = options?.Tools?
            .OfType<AIFunction>()
            .Select(static tool => tool.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            ?? Enumerable.Empty<string>();

        var instructions = options?.Instructions ?? string.Empty;
        var reasoning = MapReasoningEffort(options?.Reasoning?.Effort) ?? string.Empty;

        return string.Join(
            '\u0001',
            "tools=" + string.Join(',', toolNames),
            "instructions=" + instructions,
            "reasoning=" + reasoning);
    }

    private static string? MapReasoningEffort(ReasoningEffort? effort)
    {
        if (effort is null)
        {
            return null;
        }

        if (effort == ReasoningEffort.Low)
        {
            return "low";
        }

        if (effort == ReasoningEffort.Medium)
        {
            return "medium";
        }

        if (effort == ReasoningEffort.High)
        {
            return "high";
        }

        return null;
    }

    /// <summary>Gets the human-readable display name for this client.</summary>
    public string DisplayName => this.displayName;

    /// <summary>An <see cref="IDisposable"/> that runs an action once on disposal.</summary>
    private sealed class DelegateDisposable(Action onDispose) : IDisposable
    {
        private Action? onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref this.onDispose, null)?.Invoke();
    }
}

/// <summary>
/// The resources and operations for a single Copilot streaming turn, established under the turn lock
/// by <see cref="CopilotSdkChatClient.RunStreamingTurnAsync"/>.
/// </summary>
/// <param name="Reader">The channel the session's events are written to and the turn reads from.</param>
/// <param name="Subscription">The session-event (and steering-queue) subscription, disposed at turn end.</param>
/// <param name="SendAsync">Sends the turn's prompt to the session.</param>
/// <param name="OnCancelledAsync">
/// Invoked when the turn is cancelled, before the lock is released, to abort the in-flight CLI turn and
/// invalidate the session.
/// </param>
internal sealed record StreamingTurnContext(
    System.Threading.Channels.ChannelReader<ChatResponseUpdate> Reader,
    IDisposable Subscription,
    Func<CancellationToken, Task> SendAsync,
    Func<Task> OnCancelledAsync);
