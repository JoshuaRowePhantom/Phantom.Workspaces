using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AgentSchema;
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
///
/// When changing working-directory handling or the Copilot provider behaviour, update the workspace
/// documentation entities: <c>["documentation", "agent-options", "providers"]</c> and
/// <c>["documentation", "agent-options", "model-options"]</c>.
/// </remarks>
public sealed class CopilotSdkChatClient : IChatClient, IAsyncDisposable, ISelfInvokingToolChatClient
{
    private readonly string modelId;
    private readonly string displayName;
    private readonly string? gitHubToken;
    private readonly ILoggerFactory? loggerFactory;
    private readonly CopilotByokOptions? byokOptions;
    private readonly string? cliPath;
    private readonly ModelOptions? modelOptions;
    private readonly AgentInputQueueManager? queueManager;
    private readonly ISubAgentChatRegistry? subAgentChatRegistry;
    private readonly IGitHubAccountUpsertService? accountUpsertService;
    private readonly SemaphoreSlim sessionInitializationLock = new(1, 1);
    private readonly SemaphoreSlim turnLock = new(1, 1);

    private IRunningAgentChatFactory? runningAgentChatFactory;
    private ISubAgentTable? subAgentTable;

    private CopilotClient? copilotClient;
    private CopilotSession? copilotSession;
    private string? currentSessionSignature;
    private string? pendingResumeSessionId;
    private int disposeStarted;
    private volatile string? workingDirectoryOverride;

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
    /// The BYOK options this client was constructed with, or <see langword="null"/> when in
    /// standard Copilot auth mode. Exposed internally for factory wiring tests.
    /// </summary>
    internal CopilotByokOptions? ByokOptions => this.byokOptions;

    /// <summary>
    /// The GitHub token this client was constructed with, or <see langword="null"/> when no
    /// explicit token was provided (SDK falls back to the logged-in Copilot user). Exposed
    /// internally for factory wiring tests.
    /// </summary>
    internal string? GitHubToken => this.gitHubToken;

    /// <summary>
    /// The explicit Copilot CLI executable path this client was constructed with or read from
    /// the <c>cliPath</c> model option, or <see langword="null"/> when the SDK resolves the CLI
    /// itself. Exposed internally for factory wiring tests.
    /// </summary>
    internal string? CliPath => this.cliPath;

    /// <summary>
    /// The model options this client was constructed with, forwarded verbatim by
    /// <c>AgentFactory</c>. Exposed internally for factory wiring tests.
    /// </summary>
    internal ModelOptions? ModelOptions => this.modelOptions;

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
    /// Optional bring-your-own-key connection facts (provider string, endpoint, API key) pointing
    /// the session at a custom OpenAI-compatible endpoint instead of GitHub's hosted models. The
    /// remaining BYOK wire knobs (<c>wireApi</c>, <c>wireModel</c>, <c>headers</c>) are read from
    /// <paramref name="modelOptions"/> (issue #896).
    /// </param>
    /// <param name="cliPath">
    /// Optional explicit path to the Copilot CLI executable. When omitted, the <c>cliPath</c>
    /// model option is used; when that is also absent the SDK resolves the CLI itself.
    /// </param>
    /// <param name="queueManager">
    /// Optional input-queue manager. When supplied, items added to non-held queues during a streaming
    /// turn are forwarded to the live Copilot session as immediate steering input.
    /// </param>
    /// <param name="modelOptions">
    /// Optional model-level options from the agent definition, forwarded verbatim by the factory.
    /// The client interprets provider-specific keys such as <c>cliPath</c>, <c>wireApi</c>,
    /// <c>wireModel</c>, and <c>headers</c>; the working directory is no longer read from model
    /// options (issue #896) — it flows through <see cref="ChatOptions.AdditionalProperties"/>.
    /// </param>
    /// <param name="subAgentChatRegistry">
    /// Optional registry for sub-agent chat sessions. When supplied, sub-agent lifecycle events
    /// (<c>SubagentStartedEvent</c>, <c>SubagentCompletedEvent</c>, <c>SubagentFailedEvent</c>) are
    /// forwarded to the corresponding child <see cref="ISubAgentChat"/> sink.
    /// </param>
    /// <param name="accountUpsertService">
    /// Optional service that auto-creates a <c>user-account</c> entity when the first Copilot client
    /// session is established. When <see langword="null"/>, no upsert is performed.
    /// </param>
    public CopilotSdkChatClient(
        string modelId,
        string displayName,
        string? gitHubToken,
        ILoggerFactory? loggerFactory,
        CopilotByokOptions? byokOptions = null,
        string? cliPath = null,
        AgentInputQueueManager? queueManager = null,
        ModelOptions? modelOptions = null,
        ISubAgentChatRegistry? subAgentChatRegistry = null,
        IGitHubAccountUpsertService? accountUpsertService = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        this.modelId = modelId;
        this.displayName = displayName;
        this.gitHubToken = string.IsNullOrWhiteSpace(gitHubToken) ? null : gitHubToken;
        this.loggerFactory = loggerFactory;
        this.byokOptions = byokOptions;
        this.modelOptions = modelOptions;
        this.cliPath = string.IsNullOrWhiteSpace(cliPath) ? GetStringModelOption(modelOptions, "cliPath") : cliPath;
        this.queueManager = queueManager;
        this.subAgentChatRegistry = subAgentChatRegistry;
        this.accountUpsertService = accountUpsertService;
    }

    /// <summary>
    /// Builds the Copilot SDK <see cref="ProviderConfig"/> for a BYOK session. The provider type
    /// is derived from the agent definition's provider string (<c>openai</c> → <c>openai</c>,
    /// <c>azure-openai</c> → <c>azure</c>) and the wire knobs (<c>wireApi</c>, <c>wireModel</c>,
    /// <c>headers</c>) are read from the model options — the SDK, not the factory, is responsible
    /// for interpreting them (issue #896).
    /// </summary>
    public static ProviderConfig CreateProviderConfig(
        CopilotByokOptions byokOptions,
        string modelId,
        ModelOptions? modelOptions = null)
    {
        ArgumentNullException.ThrowIfNull(byokOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var providerConfig = new ProviderConfig
        {
            Type = MapProviderType(byokOptions.Provider),
            WireApi = GetStringModelOption(modelOptions, "wireApi") ?? "chat-completions",
            BaseUrl = byokOptions.BaseUrl,
            ApiKey = byokOptions.ApiKey,
            ModelId = modelId,
            WireModel = GetStringModelOption(modelOptions, "wireModel"),
        };

        if (modelOptions?.AdditionalProperties?.TryGetValue("headers", out var headersObj) == true
            && headersObj is System.Collections.Generic.IDictionary<string, object> rawHeaders)
        {
            providerConfig.Headers = rawHeaders
                .Where(static kvp => kvp.Value is string)
                .ToDictionary(static kvp => kvp.Key, static kvp => (string)kvp.Value!);
        }

        return providerConfig;
    }

    // Maps the agent-definition provider string to the provider type understood by the Copilot
    // runtime. Derived from the provider string rather than a model option so there is a single
    // source of truth (issue #896).
    private static string MapProviderType(string provider) => provider switch
    {
        "azure-openai" => "azure",
        _ => provider,
    };

    private static string? GetStringModelOption(ModelOptions? modelOptions, string key)
        => modelOptions?.AdditionalProperties?.TryGetValue(key, out var value) == true
            ? value as string
            : null;

    /// <summary>
    /// Builds the Copilot SDK <see cref="SessionConfig"/> for a turn, forwarding the agent's
    /// model, BYOK provider, reasoning effort, system instructions, working directory, and—critically—its
    /// function tools. The Copilot CLI otherwise only exposes its own built-in tools, so without forwarding
    /// <see cref="ChatOptions.Tools"/> the workspace <see cref="AIFunction"/>s (for example
    /// <c>workspaces_entity_get</c>) never reach the model. When <see cref="ChatOptions.ModelId"/>
    /// is set it selects the model for this session, overriding the constructor-fixed model id
    /// (issue #896).
    /// </summary>
    public static SessionConfig BuildSessionConfig(
        string modelId,
        CopilotByokOptions? byokOptions,
        ChatOptions? options,
        ModelOptions? modelOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var effectiveModelId = GetEffectiveModelId(modelId, options);

        var sessionConfig = new SessionConfig
        {
            Model = effectiveModelId,
            Streaming = true,
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        if (byokOptions is not null)
        {
            sessionConfig.Provider = CreateProviderConfig(byokOptions, effectiveModelId, modelOptions);
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

        var workingDirectory = GetWorkingDirectory(options);
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            sessionConfig.WorkingDirectory = workingDirectory;
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
    /// same model, BYOK provider, reasoning effort, system instructions, working directory, and function
    /// tools (issue #3).
    /// </summary>
    public static ResumeSessionConfig BuildResumeSessionConfig(
        string modelId,
        CopilotByokOptions? byokOptions,
        ChatOptions? options,
        ModelOptions? modelOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var effectiveModelId = GetEffectiveModelId(modelId, options);

        var resumeConfig = new ResumeSessionConfig
        {
            Model = effectiveModelId,
            Streaming = true,
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };

        if (byokOptions is not null)
        {
            resumeConfig.Provider = CreateProviderConfig(byokOptions, effectiveModelId, modelOptions);
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

        var workingDirectory = GetWorkingDirectory(options);
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            resumeConfig.WorkingDirectory = workingDirectory;
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

    /// <summary>
    /// Overrides the working directory used for the next Copilot session creation or resumption,
    /// so that <see cref="EnsureSessionAsync"/> immediately detects a signature change and resumes
    /// with the new working directory on the next turn — without waiting for the new value to be
    /// persisted to the agent-session entity's parameter store.
    /// </summary>
    internal void SetWorkingDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.workingDirectoryOverride = path;
    }

    /// <summary>
    /// The working-directory override set by <see cref="SetWorkingDirectory"/>, or
    /// <see langword="null"/> when no in-process override has been applied.
    /// Exposed internally for unit-test assertions.
    /// </summary>
    internal string? WorkingDirectoryOverride => this.workingDirectoryOverride;

    /// <summary>
    /// Injects the <see cref="IRunningAgentChatFactory"/> and <see cref="ISubAgentTable"/> that
    /// <see cref="CopilotSubAgentRouter"/> uses to create and register sub-agent
    /// <see cref="AgentChat"/> instances when a <c>SubagentStartedEvent</c> arrives.
    /// Called from <see cref="AgentChat.InitializeAsync"/> after the client has been created,
    /// in the same block that subscribes to <see cref="SteeringMessageForwarded"/> and
    /// <see cref="SessionEstablished"/>.
    /// </summary>
    internal void SetSubAgentDependencies(IRunningAgentChatFactory? factory, ISubAgentTable? table)
    {
        this.runningAgentChatFactory = factory;
        this.subAgentTable = table;
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

            var router = new CopilotSubAgentRouter(
                channel.Writer,
                this.subAgentChatRegistry,
                this.runningAgentChatFactory,
                this.subAgentTable,
                this.loggerFactory?.CreateLogger<CopilotSubAgentRouter>());

            // Fix for GitHub issue #765: serialize event dispatch via a channel to prevent concurrent
            // routing calls from corrupting internal dictionaries. SingleReader ensures the drain
            // loop is the only consumer; SingleWriter=false allows multiple SDK event callbacks to write.
            var eventChannel = Channel.CreateUnbounded<SessionEvent>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

            var eventSubscription = session.On(sessionEvent => eventChannel.Writer.TryWrite(sessionEvent));

            var dispatchLoop = Task.Run(async () =>
            {
                try
                {
                    // Issue #808 split: CopilotSdkStreamAdapter translates raw SDK events into
                    // routed ChatResponseUpdate items; CopilotSubAgentRouter interprets the
                    // stream and pushes each update into the correct sink. The adapter completes
                    // normally on SessionIdleEvent and faults on SessionErrorEvent, so the turn
                    // channel is completed here rather than inside the translation layer.
                    await foreach (var update in CopilotSdkStreamAdapter
                        .TranslateCopilotSdkSessionEvents(eventChannel.Reader, turnCancellationToken)
                        .ConfigureAwait(false))
                    {
                        await router.RouteAsync(update).ConfigureAwait(false);
                    }

                    channel.Writer.TryComplete();
                }
                catch (OperationCanceledException) when (turnCancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // Without this, a dispatch failure kills the event loop while the CLI keeps
                    // streaming: every subsequent session event is dropped and the turn's channel
                    // never completes, so the remaining output silently never renders (issue
                    // #912). Fail the turn loudly instead.
                    this.loggerFactory?.CreateLogger<CopilotSdkChatClient>().LogError(
                        exception,
                        "Session event dispatch failed; failing the turn.");
                    channel.Writer.TryComplete(exception);
                }
            }, turnCancellationToken);

            // While a turn is running, forward any Immediate-immediacy queue items as steering
            // input. SendAsync with Mode="immediate" is safe to call concurrently with a live turn.
            void OnQueueChanged(object? sender, AgentInputQueueManager.QueueStateChangedEventArgs e)
            {
                if (e.ChangeKind != AgentInputQueueManager.QueueStateChangeKind.ItemAdded)
                {
                    return;
                }

                while (this.queueManager!.TryDequeueNextImmediate(out var item))
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

            var subscription = new AsyncDelegateDisposable(async () =>
            {
                eventSubscription.Dispose();
                eventChannel.Writer.Complete();
                await dispatchLoop;
                if (this.queueManager is not null)
                {
                    this.queueManager.QueueStateChanged -= OnQueueChanged;
                }

                await router.DisposeRemainingLeasesAsync();
            });

            return new StreamingTurnContext(
                channel.Reader,
                subscription,
                sendCancellationToken => session.SendAsync(
                    messageOptions,
                    sendCancellationToken),
                () => this.AbortAndInvalidateSessionAsync(session),
                () => { this.InvalidateBrokenSession(session); return Task.CompletedTask; });
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
            // GetReadyTurnAsync handles IOException from SendAsync by retrying once with a fresh
            // session. This is kept in a separate (non-iterator) method because async iterators
            // do not permit yield inside a try block that has catch clauses.
            var turn = await this.GetReadyTurnAsync(beginTurnAsync, cancellationToken).ConfigureAwait(false);
            await using (turn.Subscription)
            {
                try
                {
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

    // Establishes a turn and sends its prompt. If SendAsync throws IOException (broken pipe from a
    // resumed session), calls OnPipeBrokenAsync to invalidate the session without re-arming
    // pendingResumeSessionId, then retries once with a fresh session.
    private async Task<StreamingTurnContext> GetReadyTurnAsync(
        Func<CancellationToken, Task<StreamingTurnContext>> beginTurnAsync,
        CancellationToken cancellationToken)
    {
        var turn = await beginTurnAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await turn.SendAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (System.IO.IOException) when (!cancellationToken.IsCancellationRequested)
        {
            // The resumed session's pipe is broken. Dispose its subscription, invalidate the session
            // without re-arming the resume id, then retry once with a brand-new session.
            await turn.Subscription.DisposeAsync();
            await turn.OnPipeBrokenAsync().ConfigureAwait(false);

            turn = await beginTurnAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await turn.SendAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await turn.Subscription.DisposeAsync();
                throw;
            }
        }

        return turn;
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

        // Re-arm the resume id so the next EnsureSessionAsync reconnects to the existing Copilot CLI
        // session (with its history) rather than creating a blank one (GitHub issue #35, Failure 1).
        this.pendingResumeSessionId = session.SessionId;

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

    // Drops the cached session without re-arming pendingResumeSessionId. Used when the session's
    // pipe broke (GitHub issue #267): the broken session cannot be resumed, so the next
    // EnsureSessionAsync must create a fresh session rather than trying to resume the broken one.
    private void InvalidateBrokenSession(CopilotSession session)
    {
        if (Interlocked.CompareExchange(ref this.copilotSession, null, session) != session)
        {
            return;
        }

        this.pendingResumeSessionId = null;
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
                    .LogDebug(exception, "Disposing the broken Copilot session failed.");
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
    /// Builds a <see cref="MessageOptions"/> from the message history by collecting all consecutive
    /// trailing user messages (the current batch) and combining their text content and data attachments
    /// into a single prompt. When multiple messages are queued in one turn their texts are joined with
    /// <c>\n\n---\n\n</c> so every queued message is visible to the model.
    /// </summary>
    internal static MessageOptions BuildMessageOptions(IEnumerable<ChatMessage> messages)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        // Collect consecutive trailing user messages — these are the batched messages for this turn.
        // Stop as soon as a non-user message is encountered so earlier historical turns are not included.
        var batchMessages = new List<ChatMessage>();
        for (var index = materialized.Count - 1; index >= 0; index--)
        {
            var message = materialized[index];
            if (message.Role != ChatRole.User)
            {
                break;
            }

            batchMessages.Add(message);
        }

        batchMessages.Reverse();

        var batchWithContent = batchMessages
            .Where(m => !string.IsNullOrEmpty(m.Text) || m.Contents.OfType<DataContent>().Any())
            .ToList();

        if (batchWithContent.Count > 0)
        {
            var texts = batchWithContent
                .Select(m => m.Text)
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();
            var combinedText = string.Join("\n\n---\n\n", texts);

            var options = new MessageOptions { Prompt = combinedText };

            var dataItems = batchWithContent
                .SelectMany(m => m.Contents.OfType<DataContent>())
                .ToList();

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
        var signature = ComputeSessionSignatureCore(options, this.workingDirectoryOverride);

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
                var clientOptions = new CopilotClientOptions
                {
                    GitHubToken = this.gitHubToken,
                    Logger = this.loggerFactory?.CreateLogger<CopilotClient>(),
                    CliPath = this.cliPath,
                };

                var workingDirectory = GetWorkingDirectory(options);
                if (!string.IsNullOrWhiteSpace(workingDirectory))
                {
                    clientOptions.Cwd = workingDirectory;
                }

                var client = new CopilotClient(clientOptions);
                await client.StartAsync(cancellationToken).ConfigureAwait(false);
                this.copilotClient = client;

                if (this.accountUpsertService is not null)
                {
                    // Resolve the token that the SDK is actually using. When this.gitHubToken is null
                    // the SDK falls back to the ambient gh CLI user, so resolve it the same way.
                    var tokenForUpsert = this.gitHubToken ?? GitHubAuthTokenResolver.Resolve();
                    if (!string.IsNullOrWhiteSpace(tokenForUpsert))
                    {
                        await this.accountUpsertService.UpsertForTokenAsync(tokenForUpsert, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            if (this.copilotSession is { } staleSession)
            {
                // Preserve the existing session id so the next CreateOrResumeSessionAsync call
                // resumes the Copilot CLI session with the updated config (e.g. new working
                // directory) rather than creating a blank new session.
                this.pendingResumeSessionId ??= staleSession.SessionId;
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
                var resumeConfig = BuildResumeSessionConfig(this.modelId, this.byokOptions, options, this.modelOptions);
                if (!string.IsNullOrWhiteSpace(this.workingDirectoryOverride))
                {
                    resumeConfig.WorkingDirectory = this.workingDirectoryOverride;
                }
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

        var sessionConfig = BuildSessionConfig(this.modelId, this.byokOptions, options, this.modelOptions);
        if (!string.IsNullOrWhiteSpace(this.workingDirectoryOverride))
        {
            sessionConfig.WorkingDirectory = this.workingDirectoryOverride;
        }
        return await this.copilotClient!.CreateSessionAsync(sessionConfig, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Computes a signature for the session-config inputs that, when changed between turns, require
    /// recreating the Copilot session (so live tool toggling, working-directory changes, and call-time
    /// model selection take effect). The BYOK provider is fixed for the lifetime of the client and is
    /// therefore not included; the model is included only as the call-time <see cref="ChatOptions.ModelId"/>
    /// override (issue #896). Tool order is ignored so that only an actual change to the tool set forces
    /// a recreation.
    /// </summary>
    public static string ComputeSessionSignature(ChatOptions? options)
        => ComputeSessionSignatureCore(options, workingDirectoryOverride: null);

    // Core implementation used by both the public static overload and EnsureSessionAsync (which
    // also factors in the in-process working-directory override set by SetWorkingDirectory).
    private static string ComputeSessionSignatureCore(ChatOptions? options, string? workingDirectoryOverride)
    {
        var toolNames = options?.Tools?
            .OfType<AIFunction>()
            .Select(static tool => tool.Name)
            .OrderBy(static name => name, StringComparer.Ordinal)
            ?? Enumerable.Empty<string>();

        var instructions = options?.Instructions ?? string.Empty;
        var reasoning = MapReasoningEffort(options?.Reasoning?.Effort) ?? string.Empty;
        var workingDirectory = workingDirectoryOverride ?? GetWorkingDirectory(options) ?? string.Empty;
        var modelOverride = string.IsNullOrWhiteSpace(options?.ModelId) ? string.Empty : options!.ModelId;

        return string.Join(
            '\u0001',
            "tools=" + string.Join(',', toolNames),
            "instructions=" + instructions,
            "reasoning=" + reasoning,
            "working-directory=" + workingDirectory,
            "model=" + modelOverride);
    }

    // Selects the model for a session: the call-time ChatOptions.ModelId when supplied, otherwise
    // the constructor-fixed model id (issue #896).
    private static string GetEffectiveModelId(string modelId, ChatOptions? options)
        => string.IsNullOrWhiteSpace(options?.ModelId) ? modelId : options!.ModelId!;

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

    // Extracts the working directory from the runtime ChatOptions.AdditionalProperties override
    // (set by AgentChat.UpdateParameterValues for the /working-directory slash command, and seeded
    // from model options by AgentFactory.ConfigureChatOptions). Model options are intentionally not
    // read here: the chat client does not honour model parameters for the working directory (issue
    // #896). The value maps to CopilotClientOptions.Cwd (process level) and
    // SessionConfig.WorkingDirectory / ResumeSessionConfig.WorkingDirectory (session level).
    private static string? GetWorkingDirectory(ChatOptions? options)
    {
        if (options?.AdditionalProperties?.TryGetValue("working-directory", out var chatValue) == true
            && chatValue is string chatDir
            && !string.IsNullOrEmpty(chatDir))
        {
            return chatDir;
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
/// <param name="OnPipeBrokenAsync">
/// Invoked when <see cref="SendAsync"/> throws <see cref="System.IO.IOException"/> on a resumed session,
/// before the retry. Invalidates the session without setting <c>pendingResumeSessionId</c> so the retry
/// creates a fresh session instead of attempting to resume the broken one again.
/// </param>
internal sealed record StreamingTurnContext(
    System.Threading.Channels.ChannelReader<ChatResponseUpdate> Reader,
    IAsyncDisposable Subscription,
    Func<CancellationToken, Task> SendAsync,
    Func<Task> OnCancelledAsync,
    Func<Task> OnPipeBrokenAsync);
