using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AgentSchema;
using GitHub.Copilot;
using TaskInfo = GitHub.Copilot.Rpc.TaskInfo;
using TaskInfoAgent = GitHub.Copilot.Rpc.TaskInfoAgent;
using EventsReadResult = GitHub.Copilot.Rpc.EventsReadResult;
using AgentRegistryLiveTargetEntry = GitHub.Copilot.Rpc.AgentRegistryLiveTargetEntry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Chat;

namespace Phantom.Workspaces.Llm;

#pragma warning disable GHCP001

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
public sealed class CopilotSdkChatClient : IChatClient, IAsyncDisposable, ISelfInvokingToolChatClient, SlashCommands.IModelSlashCommandClient, ICopilotSdkSessionSink
{
    private string modelId;
    private readonly string displayName;
    private readonly SensitiveValueLease? gitHubToken;
    private readonly AgentCredentialLeaseSet credentials;
    private readonly IAccessBroker? accessBroker;
    private readonly CredentialUseLineage credentialLineage;
    private readonly Func<AccessPresentationScope> accessPresentationScope;
    private readonly AccessRequestor? credentialRequestor;
    private readonly CredentialUseDescriptor? credentialUse;
    private readonly ICredentialAccessResult? credentialOwner;
    private readonly IAsyncDisposable? ownedSecretResolver;
    private bool accountUpsertCompleted;
    private readonly ILoggerFactory? loggerFactory;
    private readonly CopilotByokOptions? byokOptions;
    private readonly string? remoteProviderReference;
    private readonly string? cliPath;
    private readonly ModelOptions? modelOptions;
    private readonly CopilotBuiltinToolPolicy? builtinToolPolicy;
    private readonly AgentInputQueueManager? queueManager;
    private readonly ISubAgentChatRegistry? subAgentChatRegistry;
    private readonly IGitHubAccountUpsertService? accountUpsertService;
    private ICopilotClientFactory copilotClientFactory;
    private ICopilotRuntimeConnectionFactory runtimeConnectionFactory;
    private readonly object runtimeConnectionGate = new();
    private Task<CopilotRuntimeConnectionLease>? runtimeConnectionSelectionTask;

    // Issue #1443 (per-component-executor-binding): the model's executor NAME sourced from
    // model.options.executor (sibling to cliPath / wireApi / working-directory). When it resolves —
    // through the SAME shared executor-bindings path as tools — to a non-local connection-descriptor,
    // the innermost SDK session is obtained over a transport instead of the in-process CLI factory,
    // while the router and context providers stay local.
    private readonly string? modelExecutorName;
    private readonly AgentExecutionTrustContext? executionTrustContext;
    private ExecutorBindings? executorBindings;
    private ITransportFactoryRegistry? executorTransportFactoryRegistry;
    private bool requiresRemoteProvider;
    private TimeProvider? remoteSessionTimeProvider;
    private TimeSpan? remoteSessionStartupTimeout;
    private TimeSpan? remoteSessionTerminalTimeout;

    /// <summary>
    /// The account-upsert service wired in by the factory, or <see langword="null"/> when none was
    /// supplied. Exposed internally so factory-wiring tests can confirm the service is threaded
    /// through to the Copilot client (issue #1047).
    /// </summary>
    internal IGitHubAccountUpsertService? AccountUpsertService => this.accountUpsertService;

    /// <summary>
    /// The currently active model identifier.
    /// </summary>
    public string ModelId => this.modelId;

    /// <summary>Raised after the active model changes successfully.</summary>
    public event EventHandler? ModelChanged;

    private readonly SemaphoreSlim sessionInitializationLock = new(1, 1);
    private readonly SemaphoreSlim turnLock = new(1, 1);

    private IRunningAgentChatFactory runningAgentChatFactory = default!;
    private ISubAgentTable subAgentTable = default!;

    private ICopilotClient? copilotClient;
    private ICopilotSession? copilotSession;
    private string? currentSessionSignature;
    private string? pendingResumeSessionId;
    private readonly object disposalGate = new();
    private Task? disposalTask;
    private readonly object subAgentStateGate = new();
    private readonly SemaphoreSlim liveSessionFetchGate = new(1, 1);
    private long liveSessionGeneration;
    internal event Action<IReadOnlyList<AgentRegistryLiveTargetEntry>>? LiveSessionSnapshotApplied;
    internal event Action<Guid>? StateEventProcessed;
    private readonly CopilotSubAgentLifecycleStore subAgentStates = new();
    private readonly Dictionary<string, SubAgent> childrenByAgentId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubAgent> childrenByToolCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunningAgentChatLease> liveChildLeases = new(StringComparer.Ordinal);
    private IReadOnlyList<TaskInfo> lastTaskSnapshot = [];
    private IReadOnlyList<AgentRegistryLiveTargetEntry> lastLiveSessionSnapshot = [];
    private IDisposable? stateSubscription;
    private CopilotSdkSubAgentStateReconciler? stateReconciler;
    private CopilotSubAgentRouter? currentRouter;
    private CancellationTokenSource? stateObserverCancellation;
    private Task? stateObserverPump;
    private Task? liveSessionPoller;
    private string? persistedEventCursor;
    private readonly HashSet<Guid> seenLifecycleEvents = [];
    private readonly List<SessionEvent> pendingLifecycleEvents = [];
    private readonly Dictionary<Guid, long> lifecycleGenerations = [];
    private long lastLiveSessionBoundary;
    private readonly Dictionary<string, (string ChildKey, string Invocation)> taskBindings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> lifecycleDiagnosticCorrelations = new(StringComparer.Ordinal);
    private int lifecycleDiagnosticCount;
    private DateTimeOffset? lastObservedSdkTimestamp;
    private readonly Dictionary<Guid, TaskCompletionSource> terminalObservationBarriers = [];
    private readonly Dictionary<Guid, long?> acceptedTerminalGenerations = [];
    private Exception? stateObserverFailure;
    internal TimeProvider StateObservationTimeProvider { get; set; } = TimeProvider.System;
    private readonly List<Task> childRetirementTasks = [];
    private CopilotSdkRootEventQueue? rootEventQueue;
    private readonly List<Task> admittedSends = [];
    internal const string ConsumeContinuationOptionName = "copilot.sdk.consume-continuation";
    internal event Action? RootContinuationAvailable;
    internal bool HasPendingRootContinuation => this.rootEventQueue?.HasPending == true;
    internal void SealRootOutput() => this.rootEventQueue?.SealAccepted();

    // Receipt acceptance and the native call's start share one linearization point. The
    // framework may then append the committed prompt before consuming any routed response.
    // If that append fails, AgentChat transfers the accepted reader to a drain-only run.
    internal async Task<CopilotSdkRootBatch> AdmitRootRequestAsync(
        CopilotSdkRootBatch? reservation,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options,
        Func<bool> admissionOpen,
        CancellationToken cancellationToken)
    {
        await this.turnLock.WaitAsync(reservation?.HasAcceptedOutput == true
            ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.rootEventQueue is { } previous
                && this.currentSessionSignature != ComputeSessionSignatureCore(options, this.workingDirectoryOverride))
            {
                previous.SealAccepted();
                if (reservation?.HasAcceptedOutput == true)
                    return reservation.AsContinuation();
                reservation?.Dispose();
                reservation = null;
                if (previous.HasPending)
                    return new(previous, previous.Acquire(true), true);
            }
            if (reservation?.HasAcceptedOutput == true)
                return reservation.AsContinuation();
            if (!admissionOpen()) throw new OperationCanceledException("Parent request admission stopped.");
            cancellationToken.ThrowIfCancellationRequested();
            var session = await this.EnsureSessionAsync(options, cancellationToken).ConfigureAwait(false);
            var owner = this.rootEventQueue!;
            if (reservation is not null && !ReferenceEquals(reservation.Owner, owner))
            {
                reservation.Dispose();
                reservation = null;
            }
            reservation ??= new(owner, owner.Acquire(false, out var continuation), continuation);
            if (reservation.HasAcceptedOutput)
                return reservation.AsContinuation();
            lock (this.subAgentStateGate)
                if (this.stateObserverFailure is { } observationFailure)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(observationFailure).Throw();
            if (!owner.TryStartRequest(reservation.Reader,
                () => session.SendAsync(BuildMessageOptions(messages), cancellationToken),
                out var sending, () =>
                {
                    if (!admissionOpen()) throw new OperationCanceledException("Parent request admission stopped.");
                    cancellationToken.ThrowIfCancellationRequested();
                }))
                return reservation.AsContinuation();
            reservation.RequestSend = sending;
            lock (this.admittedSends)
            {
                this.admittedSends.RemoveAll(task => task.IsCompletedSuccessfully);
                this.admittedSends.Add(ObserveAdmittedSendAsync(sending!, cancellationToken));
            }
            return reservation;
        }
        finally { this.turnLock.Release(); }
    }

    private static async Task ObserveAdmittedSendAsync(Task sending, CancellationToken cancellationToken)
    {
        try { await sending.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    internal async Task RetireAdmittedRequestAsync()
    {
        if (this.copilotSession is { } session)
            await this.AbortAndInvalidateSessionAsync(session, this.currentRouter).ConfigureAwait(false);
        Task[] sends;
        lock (this.admittedSends)
        {
            sends = this.admittedSends.ToArray();
            this.admittedSends.Clear();
        }
        await CompleteOwnedCleanupAsync(sends.Select<Task, Func<Task>>(task => () => task).ToArray()).ConfigureAwait(false);
    }

    internal CopilotSdkRootBatch? ReserveRootBatch(bool continuation)
    {
        if (this.copilotSession is null) return null;
        var owner = this.rootEventQueue;
        if (owner is null) return null;
        var reader = owner.Acquire(continuation, out var acquiredContinuation);
        return new(owner, reader, acquiredContinuation);
    }
    private volatile string? workingDirectoryOverride;

    /// <summary>
    /// Gate used by the live-turn's <c>OnQueueChanged</c> handler. Set to <see langword="true"/>
    /// at the start of <see cref="AbortAndInvalidateSessionAsync"/> (before the session is
    /// invalidated) so a user message enqueued during teardown is neither steered to the dying
    /// CLI session nor dequeued-and-dropped. Reset per turn in <c>BeginTurnAsync</c>. See
    /// GitHub issue #1142.
    /// </summary>
    private volatile bool steeringSuspended;

    /// <summary>
    /// Test hook: exposes <see cref="steeringSuspended"/> so tests can assert teardown ordering
    /// and simulate the teardown gate without spinning up a real Copilot CLI session.
    /// </summary>
    internal bool SteeringSuspendedForTest
    {
        get => this.steeringSuspended;
        set => this.steeringSuspended = value;
    }

    /// <summary>
    /// Raised when a steering message is forwarded to the live Copilot session so the owning
    /// <c>AgentChat</c> can record it in its visible chat history.
    /// </summary>
    internal event Action<ChatMessage>? SteeringMessageForwarded;
    internal event Func<ChatMessage, Exception, Task>? SteeringDeliveryFailed;
    private readonly List<Task<SteeringDeliveryOutcome>> steeringDeliveries = [];

    internal Task<SteeringDeliveryOutcome[]> WaitForSteeringDeliveriesAsync()
    {
        lock (this.steeringDeliveries) return Task.WhenAll(this.steeringDeliveries.ToArray());
    }

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
    internal SensitiveValueLease? GitHubToken => this.gitHubToken;

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

    internal CopilotBuiltinToolPolicy? BuiltinToolPolicyForTest => this.builtinToolPolicy;

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
        SensitiveValueLease? gitHubToken,
        ILoggerFactory? loggerFactory,
        CopilotByokOptions? byokOptions = null,
        string? cliPath = null,
        AgentInputQueueManager? queueManager = null,
        ModelOptions? modelOptions = null,
        ISubAgentChatRegistry? subAgentChatRegistry = null,
        IGitHubAccountUpsertService? accountUpsertService = null,
        SlashCommands.ISlashCommandRegistry? slashCommandRegistry = null,
        CopilotBuiltinToolPolicy? builtinToolPolicy = null,
        AgentExecutionTrustContext? executionTrustContext = null,
        IAccessBroker? accessBroker = null,
        CredentialUseLineage? credentialLineage = null,
        ISecretPlaceholderResolver? secretPlaceholderResolver = null,
        ICredentialAccessResult? credentialOwner = null,
        IAsyncDisposable? ownedSecretResolver = null,
        AccessPresentationScope accessPresentationScope = AccessPresentationScope.Runtime,
        AccessRequestor? credentialRequestor = null,
        CredentialUseDescriptor? credentialUse = null,
        Func<AccessPresentationScope>? accessPresentationScopeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        this.modelId = modelId;
        this.displayName = displayName;
        this.gitHubToken = gitHubToken;
        this.credentials = new AgentCredentialLeaseSet(
            byokOptions?.ApiKeyLease ?? gitHubToken,
            modelOptions,
            secretPlaceholderResolver);
        this.accessBroker = accessBroker;
        this.credentialLineage = credentialLineage ?? new CredentialUseLineage();
        this.accessPresentationScope = accessPresentationScopeProvider ?? (() => accessPresentationScope);
        this.credentialRequestor = credentialRequestor;
        this.credentialUse = credentialUse;
        this.credentialOwner = credentialOwner;
        this.ownedSecretResolver = ownedSecretResolver;
        this.loggerFactory = loggerFactory;
        this.byokOptions = byokOptions;
        this.remoteProviderReference = GetStringModelOption(modelOptions, "remoteProvider");
        this.modelOptions = modelOptions;
        this.builtinToolPolicy = builtinToolPolicy;
        this.cliPath = string.IsNullOrWhiteSpace(cliPath) ? GetStringModelOption(modelOptions, "cliPath") : cliPath;
        this.modelExecutorName = GetStringModelOption(modelOptions, "executor");
        this.executionTrustContext = executionTrustContext;
        this.queueManager = queueManager;
        this.subAgentChatRegistry = subAgentChatRegistry;
        this.accountUpsertService = accountUpsertService;
        this.copilotClientFactory = DefaultCopilotClientFactory.Instance;
        this.runtimeConnectionFactory = new CopilotRuntimeConnectionFactory();

        if (slashCommandRegistry is { } registry)
        {
            registry.Register(new SlashCommands.CopilotSdkWorkingDirectorySlashCommandHandler(this));
            registry.Register(new SlashCommands.CopilotSdkModelSlashCommandHandler(this));
        }
    }

    /// <summary>
    /// Internal helper for test injection: sets a custom <see cref="ICopilotClientFactory"/>
    /// after construction.
    /// </summary>
    internal void SetCopilotClientFactoryForTest(ICopilotClientFactory factory)
    {
        this.copilotClientFactory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    internal void SetRuntimeConnectionFactoryForTest(ICopilotRuntimeConnectionFactory factory)
    {
        this.runtimeConnectionFactory = factory
            ?? throw new ArgumentNullException(nameof(factory));
        this.runtimeConnectionSelectionTask = null;
    }

    /// <summary>
    /// Supplies the shared executor-bindings map and transport registry used to honour a
    /// <c>model.options.executor</c> binding (issue #1443). When the model's resolved
    /// connection-descriptor is non-local, <see cref="EnsureSessionAsync"/> obtains the innermost SDK
    /// session over a transport instead of the in-process CLI factory. Passing <see langword="null"/>
    /// for either argument keeps the behaviour-preserving in-process path.
    /// </summary>
    internal void ConfigureExecutorRouting(
        ExecutorBindings? bindings,
        ITransportFactoryRegistry? registry,
        bool requiresRemoteProvider = false)
    {
        this.executorBindings = bindings;
        this.executorTransportFactoryRegistry = registry;
        this.requiresRemoteProvider |= requiresRemoteProvider;
    }

    internal void ConfigureRemoteSessionLifecycleForTest(
        TimeProvider timeProvider,
        TimeSpan startupTimeout,
        TimeSpan terminalTimeout)
    {
        this.remoteSessionTimeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        this.remoteSessionStartupTimeout = startupTimeout;
        this.remoteSessionTerminalTimeout = terminalTimeout;
    }

    /// <summary>
    /// Test seam (issue #1443): resolves the model's executor binding to a session-only remote
    /// <see cref="ICopilotClient"/>, or <see langword="null"/> when the model runs in-process. Exposes
    /// the private routing decision so it can be asserted without spinning up a live streaming turn.
    /// </summary>
    internal Task<ICopilotClient?> ResolveRemoteClientForTestAsync(CancellationToken cancellationToken = default)
        => this.TryCreateRemoteClientAsync(cancellationToken);

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
        ModelOptions? modelOptions = null,
        CopilotBuiltinToolPolicy? builtinToolPolicy = null)
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

        var tools = options?.Tools?.OfType<AIFunctionDeclaration>().ToList();
        if (tools is { Count: > 0 })
        {
            sessionConfig.Tools = tools;
        }

        ApplyBuiltinToolPolicy(sessionConfig, builtinToolPolicy);

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
        ModelOptions? modelOptions = null,
        CopilotBuiltinToolPolicy? builtinToolPolicy = null)
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

        var tools = options?.Tools?.OfType<AIFunctionDeclaration>().ToList();
        if (tools is { Count: > 0 })
        {
            resumeConfig.Tools = tools;
        }

        ApplyBuiltinToolPolicy(resumeConfig, builtinToolPolicy);

        return resumeConfig;
    }

    private static void ApplyBuiltinToolPolicy(SessionConfig config, CopilotBuiltinToolPolicy? policy)
    {
        if (policy?.AvailableTools is { } available)
        {
            config.AvailableTools = available.ToList();
        }

        if (policy?.ExcludedTools is { } excluded)
        {
            config.ExcludedTools = excluded.ToList();
        }
    }

    private static void ApplyBuiltinToolPolicy(ResumeSessionConfig config, CopilotBuiltinToolPolicy? policy)
    {
        if (policy?.AvailableTools is { } available)
        {
            config.AvailableTools = available.ToList();
        }

        if (policy?.ExcludedTools is { } excluded)
        {
            config.ExcludedTools = excluded.ToList();
        }
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

    internal bool IsLocalDocumentFileSystem => !this.requiresRemoteProvider
        && (this.executorBindings is null
            ? string.IsNullOrWhiteSpace(this.modelExecutorName)
            : IsLocalDescriptor(this.executorBindings.ResolveComponent(this.modelExecutorName)));

    /// <summary>
    /// Changes the active model for this client. When a session is already live, the model is
    /// retuned in place via <see cref="ICopilotSession.SetModelAsync"/> so the conversation history
    /// is preserved (issue #1418); the cached session signature is left intact so the next turn
    /// reuses the same session rather than recreating it. When no session is live yet, the model id
    /// is simply stashed and applied by the next created/resumed session.
    /// </summary>
    internal Func<IDisposable>? EnterModelMutation { get; set; }

    public async Task SetModelIdAsync(string modelId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        using var mutation = this.EnterModelMutation?.Invoke();

        if (this.copilotSession is { } session)
        {
            await session.SetModelAsync(modelId, cancellationToken).ConfigureAwait(false);
        }

        this.modelId = modelId;
        this.ModelChanged?.Invoke(this, EventArgs.Empty);

        // No live session yet: BuildSessionConfig/BuildResumeSessionConfig already read this.modelId
        // when the next session is created or resumed.
    }

    /// <summary>
    /// Returns the models available from the Copilot backend.
    /// Ensures the <see cref="CopilotClient"/> is started before calling
    /// <see cref="CopilotClient.ListModelsAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<GitHub.Copilot.ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        await this.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await this.copilotClient!.ListModelsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the <see cref="CopilotClient"/> is started without creating a full session.
    /// </summary>
    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (this.copilotClient is not null)
        {
            return;
        }

        await this.sessionInitializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.copilotClient is not null)
            {
                return;
            }

            var client = await this.TryCreateRemoteClientAsync(cancellationToken).ConfigureAwait(false)
                ?? await this.CreateClientAsync(workingDirectory: null, cancellationToken).ConfigureAwait(false);
            try
            {
                await StartClientAsync(client, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    await DisposeRuntimeConnectionSelectionAsync().ConfigureAwait(false);
                }
                throw;
            }
            this.copilotClient = client;
        }
        finally
        {
            this.sessionInitializationLock.Release();
        }
    }

    /// <summary>
    /// Starts the Copilot client, translating the SDK's raw "Copilot runtime not found" failure
    /// into a message that makes sense for an installed, signed application (issue #1376). The SDK
    /// resolves its CLI strictly from <c>AppContext.BaseDirectory\runtimes\&lt;rid&gt;\native\copilot.exe</c>
    /// and, when it is missing, tells the caller to "restore the NuGet package" — advice that is
    /// meaningless to an end user of a packaged build. Phantom.Workspaces bundles that runtime as a
    /// loose file in the installed payload, so a missing runtime means the installation is damaged;
    /// point the user at reinstalling or at the manual <c>cliPath</c> override instead.
    /// </summary>
    private static async Task StartClientAsync(ICopilotClient client, CancellationToken cancellationToken)
    {
        try
        {
            await client.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (IsRuntimeNotFound(ex))
        {
            throw new InvalidOperationException(RuntimeMissingMessage, ex);
        }
    }

    internal Task<CopilotClientOptions> CreateClientOptionsForTestAsync(
        string? workingDirectory,
        CancellationToken cancellationToken = default) =>
        CreateClientOptionsAsync(workingDirectory, cancellationToken);

    private async Task<CopilotClientOptions> CreateClientOptionsAsync(
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var selection = await GetRuntimeConnectionSelectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var clientOptions = new CopilotClientOptions
        {
            Logger = this.loggerFactory?.CreateLogger<CopilotClient>(),
            Mode = this.builtinToolPolicy?.ClientMode ?? CopilotClientMode.CopilotCli,
            Connection = selection?.Connection
                ?? (!string.IsNullOrWhiteSpace(this.cliPath)
                    ? RuntimeConnection.ForStdio(this.cliPath)
                    : null),
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            clientOptions.WorkingDirectory = workingDirectory;
        if (clientOptions.Mode == CopilotClientMode.Empty)
        {
            clientOptions.BaseDirectory = !string.IsNullOrWhiteSpace(workingDirectory)
                ? workingDirectory
                : Directory.GetCurrentDirectory();
        }

        var diagnosticLogger = this.loggerFactory?
            .CreateLogger<CopilotRuntimeConnectionFactory>();
        foreach (var diagnostic in (selection?.Diagnostics ?? []).Where(
                     item => item.Severity == TrustProfilePolicyDiagnosticSeverity.Warning))
        {
            diagnosticLogger?.LogWarning(
                "Copilot MXC policy warning {Code}: {Message}",
                diagnostic.Code,
                diagnostic.Message);
        }

        return clientOptions;
    }

    private async Task<ICopilotClient> CreateClientAsync(
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var options = await this.CreateClientOptionsAsync(workingDirectory, cancellationToken).ConfigureAwait(false);
        return await this.credentials.UseAsync((material, _, _) =>
        {
            options.GitHubToken = this.byokOptions is null ? material : null;
            options.UseLoggedInUser = this.gitHubToken is null ? null : false;
            var client = this.copilotClientFactory.Create(options);
            return Task.FromResult<ICopilotClient>(new CredentialBoundCopilotClient(
                client,
                this.credentials,
                this.byokOptions));
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CopilotRuntimeConnectionLease?> GetRuntimeConnectionSelectionAsync(
        CancellationToken cancellationToken)
    {
        if (this.executionTrustContext is null)
            return null;

        Task<CopilotRuntimeConnectionLease> selectionTask;
        lock (this.runtimeConnectionGate)
        {
            selectionTask = this.runtimeConnectionSelectionTask
                ??= this.runtimeConnectionFactory.CreateConnectionAsync(
                    this.executionTrustContext,
                    this.cliPath,
                    CancellationToken.None);
        }
        return await selectionTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask DisposeRuntimeConnectionSelectionAsync()
    {
        Task<CopilotRuntimeConnectionLease>? selectionTask;
        lock (this.runtimeConnectionGate)
        {
            selectionTask = this.runtimeConnectionSelectionTask;
            this.runtimeConnectionSelectionTask = null;
        }
        if (selectionTask is null)
            return;
        try
        {
            var selection = await selectionTask.ConfigureAwait(false);
            await selection.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // A failed selection cannot own a successfully returned policy lease.
        }
    }

    /// <summary>
    /// True when the exception is the SDK's "Copilot runtime not found" failure raised because the
    /// packaged <c>copilot.exe</c> is absent from <c>AppContext.BaseDirectory\runtimes\...\native</c>.
    /// </summary>
    private static bool IsRuntimeNotFound(InvalidOperationException ex) =>
        ex.Message.Contains("Copilot runtime not found", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The installed-app-friendly replacement for the SDK's runtime-not-found message. It must not
    /// tell a signed-installer user to "restore the NuGet package" (issue #1376).
    /// </summary>
    internal const string RuntimeMissingMessage =
        "The packaged GitHub Copilot runtime (copilot.exe) is missing from this installation. It is " +
        "expected next to the application at runtimes\\<rid>\\native\\copilot.exe. Reinstall or " +
        "repair Phantom.Workspaces to restore the bundled runtime, or set the 'cliPath' model option " +
        "(equivalently RuntimeConnection.ForStdio) to point at an installed GitHub Copilot CLI.";

    /// <summary>
    /// Injects the <see cref="IRunningAgentChatFactory"/> and <see cref="ISubAgentTable"/> that
    /// <see cref="CopilotSubAgentRouter"/> uses to create and register sub-agent
    /// <see cref="AgentChat"/> instances when a <c>SubagentStartedEvent</c> arrives.
    /// Called from <see cref="AgentChat.InitializeAsync"/> after the client has been created.
    /// Fix #1109/#1110: both dependencies are now mandatory — the router no longer has a
    /// registry-only fallback, and passing null would silently misroute sub-agent output into
    /// the parent transcript at construction time.
    /// </summary>
    internal void SetSubAgentDependencies(IRunningAgentChatFactory factory, ISubAgentTable table)
    {
        this.runningAgentChatFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        this.subAgentTable = table ?? throw new ArgumentNullException(nameof(table));
    }

    private void RegisterSubAgent(string agentId, string? toolCallId, SubAgent child)
    {
        lock (this.subAgentStateGate)
        {
            if (!string.IsNullOrEmpty(agentId)) this.childrenByAgentId[agentId] = child;
            if (!string.IsNullOrEmpty(toolCallId)) this.childrenByToolCallId[toolCallId] = child;
            this.ApplyPendingLifecycleEvents(child);
            if (toolCallId is { Length: > 0 } && !this.subAgentStates.HasInvocation(toolCallId))
                this.subAgentStates.Apply(new(child.SessionId.Value, CopilotSubAgentLifecycleLayer.Invocation,
                    "running", InvocationToolCallId: toolCallId));
            foreach (var entry in this.lastLiveSessionSnapshot.Where(e => e.SessionId == agentId))
                this.ApplyRegistryEntry(entry, this.lastLiveSessionBoundary);
            this.ProjectSubAgent(child, "running");
            foreach (var task in this.lastTaskSnapshot.OfType<TaskInfoAgent>()
                         .Where(t => t.ToolCallId == toolCallId || t.Id == agentId))
                this.ApplyTask(task);
        }
    }

    internal void RegisterRestoredSubAgent(SubAgent child)
    {
        if (!CopilotSubAgentIdentity.TryRead(child.SessionId.Value, out var agentId, out var toolCallId))
            return;
        lock (this.subAgentStateGate)
        {
            this.childrenByAgentId[agentId] = child;
            if (toolCallId.Length != 0) this.childrenByToolCallId[toolCallId] = child;
            if (child.LifecycleState is { } persistedState && toolCallId.Length != 0
                && !this.subAgentStates.HasInvocation(toolCallId))
                this.subAgentStates.Apply(new(child.SessionId.Value, CopilotSubAgentLifecycleLayer.Invocation,
                    persistedState, InvocationToolCallId: toolCallId, ObservedAt: child.LifecycleObservedAt));
            this.ApplyPendingLifecycleEvents(child);
            foreach (var entry in this.lastLiveSessionSnapshot.Where(e => e.SessionId == agentId))
                this.ApplyRegistryEntry(entry, this.lastLiveSessionBoundary);
            foreach (var task in this.lastTaskSnapshot.OfType<TaskInfoAgent>()
                         .Where(t => t.Id == agentId || t.ToolCallId == toolCallId))
                this.ApplyTask(task);
        }
    }

    private async Task RefreshLiveSessionStatesAsync(CancellationToken cancellationToken)
    {
        if (this.copilotClient is null) return;
        await this.liveSessionFetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long boundary;
            lock (this.subAgentStateGate) boundary = this.subAgentStates.Generation;
            var entries = await this.copilotClient.ListLiveSessionStatesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var revision in entries.Select(entry => entry.StatusRevision).OfType<long>())
                this.liveSessionGeneration = Math.Max(this.liveSessionGeneration, revision);
            var generation = ++this.liveSessionGeneration;
            lock (this.subAgentStateGate)
            {
                var present = entries.Select(entry => entry.SessionId).ToHashSet(StringComparer.Ordinal);
                foreach (var old in this.lastLiveSessionSnapshot.Where(entry =>
                             entry.SessionId is { } id && !present.Contains(id)))
                {
                    if (this.childrenByAgentId.TryGetValue(old.SessionId!, out var child))
                    {
                        this.subAgentStates.RemoveSession(child.SessionId.Value, boundary);
                        this.ProjectSubAgent(child, "completed");
                    }
                }
                this.lastLiveSessionSnapshot = entries.Select(entry => new AgentRegistryLiveTargetEntry
                {
                    SessionId = entry.SessionId,
                    Status = entry.Status,
                    StatusRevision = entry.StatusRevision ?? generation,
                }).ToArray();
                this.lastLiveSessionBoundary = boundary;
                foreach (var entry in this.lastLiveSessionSnapshot) this.ApplyRegistryEntry(entry, boundary);
            }
            this.LiveSessionSnapshotApplied?.Invoke(this.lastLiveSessionSnapshot);
        }
        finally { this.liveSessionFetchGate.Release(); }
    }

    private void ApplyRegistryEntry(AgentRegistryLiveTargetEntry entry, long? executionBoundary = null)
    {
        if (entry.SessionId is not { } sessionId || entry.Status is not { } status
            || !this.childrenByAgentId.TryGetValue(sessionId, out var child))
        {
            this.LogLifecycleDecisionCore("host-sessions", "reconcile", "uncorrelated");
            return;
        }
        var applied = this.subAgentStates.Apply(new(child.SessionId.Value, CopilotSubAgentLifecycleLayer.Session,
            status.Value, StatusRevision: entry.StatusRevision, ExecutionBoundary: executionBoundary));
        if (applied) this.ProjectSubAgent(child, status.Value);
        this.LogLifecycleDecisionCore("host-sessions", "reconcile", applied ? "applied" : "stale", child.SessionId.Value);
    }

    internal bool IsInvocationTerminal(string toolCallId)
    {
        lock (this.subAgentStateGate) return this.subAgentStates.IsInvocationTerminal(toolCallId);
    }

    private void ApplyPendingLifecycleEvents(SubAgent child)
    {
        foreach (var e in this.pendingLifecycleEvents.Where(e =>
                     this.childrenByAgentId.GetValueOrDefault(e.AgentId ?? "") == child
                     || (GetInvocationId(e) is { } id && this.childrenByToolCallId.GetValueOrDefault(id) == child))
                 .ToArray())
        {
            this.pendingLifecycleEvents.Remove(e);
            this.ObserveSubAgentLifecycle(e);
        }
    }

    private static string? GetInvocationId(SessionEvent e) => e switch
    {
        SubagentStartedEvent started => started.Data?.ToolCallId,
        SubagentCompletedEvent completed => completed.Data?.ToolCallId,
        SubagentFailedEvent failed => failed.Data?.ToolCallId,
        _ => null,
    };

    private void ApplyTaskSnapshot(IReadOnlyList<TaskInfo> snapshot)
    {
        lock (this.subAgentStateGate)
        {
            var previousGenerations = snapshot.OfType<TaskInfoAgent>()
                .ToDictionary(task => task.Id, task => this.subAgentStates.InvocationGeneration(task.ToolCallId),
                    StringComparer.Ordinal);
            this.lastTaskSnapshot = snapshot;
            foreach (var task in snapshot.OfType<TaskInfoAgent>())
                if (this.FindTaskChild(task) is { } bound)
                    this.taskBindings[task.Id] = (bound.SessionId.Value, task.ToolCallId);
            var requests = snapshot.OfType<TaskInfoAgent>().Select(task =>
            {
                var child = this.FindTaskChild(task);
                return child is null ? null : new ApplySubAgentStateRequest(
                    child.SessionId.Value, CopilotSubAgentLifecycleLayer.Task,
                    task.Status.Value, TaskId: task.Id, InvocationToolCallId: task.ToolCallId,
                    ObservedAt: this.lastObservedSdkTimestamp);
            }).OfType<ApplySubAgentStateRequest>().ToArray();
            this.subAgentStates.ReplaceTasks(requests);
            foreach (var task in snapshot.OfType<TaskInfoAgent>())
            {
                if (task.Status == GitHub.Copilot.Rpc.TaskStatus.Running
                    && previousGenerations[task.Id] != this.subAgentStates.InvocationGeneration(task.ToolCallId)
                    && this.FindTaskChild(task)?.AgentChat is { } resumed
                    && resumed.GetService(typeof(ICopilotSubAgentReceiver)) is CopilotSubAgentChatClient receiver
                    && receiver.BeginInvocation())
                    resumed.RequestHostedFollowUp();
            }
            foreach (var child in this.childrenByAgentId.Values.Concat(this.childrenByToolCallId.Values).Distinct())
            {
                var task = snapshot.OfType<TaskInfoAgent>().LastOrDefault(t => this.FindTaskChild(t) == child);
                this.ProjectSubAgent(child, task?.Status.Value
                    ?? this.subAgentStates.LastNonrunningTaskState(child.SessionId.Value)
                    ?? "completed");
                this.LogLifecycleDecisionCore("tasks", "reconcile", task is null ? "evicted-history-retained" : "applied", child.SessionId.Value);
                if (task is not null && !this.subAgentStates.IsActive(child.SessionId.Value)
                    && child.AgentChat?.GetService(typeof(ICopilotSubAgentReceiver)) is ICopilotSubAgentReceiver receiver)
                {
                    var outcome = this.subAgentStates.Project(child.SessionId.Value);
                    if (outcome == "cancelled")
                        receiver.Fail(new OperationCanceledException("Sub-agent task was cancelled."));
                    else if (outcome == "failed")
                        receiver.Fail(new AgentSubagentFailedException("Sub-agent task failed."));
                    else if (task.Status == GitHub.Copilot.Rpc.TaskStatus.Idle
                        || task.Status == GitHub.Copilot.Rpc.TaskStatus.Completed)
                        receiver.Complete();
                }
            }
        }
    }

    private SubAgent? FindTaskChild(TaskInfoAgent task) =>
        this.childrenByToolCallId.GetValueOrDefault(task.ToolCallId)
        ?? this.childrenByAgentId.GetValueOrDefault(task.Id);

    private void ApplyTask(TaskInfoAgent task)
    {
        var child = this.FindTaskChild(task);
        if (child is null) return;
        this.taskBindings[task.Id] = (child.SessionId.Value, task.ToolCallId);
        this.subAgentStates.Configure(child.SessionId.Value, new(ExecutionMode: task.ExecutionMode?.Value));
        this.subAgentStates.Apply(new(child.SessionId.Value, CopilotSubAgentLifecycleLayer.Task,
            task.Status.Value, TaskId: task.Id, InvocationToolCallId: task.ToolCallId,
            ObservedAt: this.lastObservedSdkTimestamp));
        if (task.Status == GitHub.Copilot.Rpc.TaskStatus.Completed
            || task.Status == GitHub.Copilot.Rpc.TaskStatus.Failed
            || task.Status == GitHub.Copilot.Rpc.TaskStatus.Cancelled)
            this.subAgentStates.Apply(new(child.SessionId.Value, CopilotSubAgentLifecycleLayer.Invocation,
                task.Status.Value, InvocationToolCallId: task.ToolCallId));
        this.ProjectSubAgent(child, task.Status.Value);
    }

    private void ReprojectAfterInvocation(string agentId, string? toolCallId)
    {
        lock (this.subAgentStateGate)
        {
            var child = this.childrenByAgentId.GetValueOrDefault(agentId)
                ?? (toolCallId is not null ? this.childrenByToolCallId.GetValueOrDefault(toolCallId) : null);
            if (child is null) return;
            this.ProjectSubAgent(child, this.subAgentStates.Project(child.SessionId.Value));
        }
    }

    private void ProjectSubAgent(SubAgent child, string terminalState, Exception? terminalError = null)
    {
        var state = this.subAgentStates.Project(child.SessionId.Value);
        var request = new ApplySubAgentStateRequest(child.SessionId.Value,
            CopilotSubAgentLifecycleLayer.Session, state);
        if (child.AgentChat is { } chat)
        {
            chat.ApplySubAgentLifecycleState(request);
            if (state != "running"
                && chat.GetService(typeof(ICopilotSubAgentReceiver)) is ICopilotSubAgentReceiver receiver)
            {
                if (state == "cancelled") receiver.Fail(new OperationCanceledException("Sub-agent execution was cancelled."));
                else if (state == "failed") receiver.Fail(terminalError ?? new AgentSubagentFailedException("Sub-agent task failed."));
                else receiver.Complete();
            }
        }
        else child.SetRestoredLifecycleState(state);
    }

    private void ObserveSubAgentLifecycle(SessionEvent sessionEvent)
    {
        if (sessionEvent is AssistantTurnStartEvent && string.IsNullOrEmpty(sessionEvent.AgentId)) return;
        if (sessionEvent is SystemNotificationEvent notification)
        {
            this.ObserveTaskNotification(notification);
            return;
        }
        if (sessionEvent is not (SubagentStartedEvent or SubagentCompletedEvent or SubagentFailedEvent
            or SubagentConfiguredEvent or AssistantTurnStartEvent))
            return;

        lock (this.subAgentStateGate)
        {
            if (sessionEvent.Id != Guid.Empty && this.seenLifecycleEvents.Contains(sessionEvent.Id)) return;
            if (sessionEvent is SubagentStartedEvent && sessionEvent.Id != Guid.Empty
                && !this.lifecycleGenerations.ContainsKey(sessionEvent.Id))
                this.lifecycleGenerations[sessionEvent.Id] = this.subAgentStates.ReserveGeneration();
            var toolCallId = GetInvocationId(sessionEvent);
            var child = (!string.IsNullOrEmpty(sessionEvent.AgentId)
                ? this.childrenByAgentId.GetValueOrDefault(sessionEvent.AgentId)
                : null) ?? (!string.IsNullOrEmpty(toolCallId)
                    ? this.childrenByToolCallId.GetValueOrDefault(toolCallId)
                    : null);
            if (child is null)
            {
                if (sessionEvent.Id == Guid.Empty || this.pendingLifecycleEvents.All(e => e.Id != sessionEvent.Id))
                    this.pendingLifecycleEvents.Add(sessionEvent);
                return;
            }
            if (sessionEvent is SubagentConfiguredEvent configured)
            {
                this.subAgentStates.Configure(child.SessionId.Value,
                    new(MultiTurn: configured.Data.MultiTurn, Model: configured.Data.Model));
                return;
            }
            if (sessionEvent is SubagentStartedEvent started)
                this.subAgentStates.Configure(child.SessionId.Value, new(
                    ExecutionMode: started.Data.ExecutionMode, ParentTaskId: started.Data.ParentId, Model: started.Data.Model));
            if (sessionEvent is AssistantTurnStartEvent)
                toolCallId = this.subAgentStates.CurrentInvocationId(child.SessionId.Value);
            if (string.IsNullOrEmpty(toolCallId)) return;
            if (sessionEvent.Id != Guid.Empty) this.seenLifecycleEvents.Add(sessionEvent.Id);
            var state = sessionEvent switch
            {
                SubagentStartedEvent or AssistantTurnStartEvent => "running",
                SubagentFailedEvent => "failed",
                SubagentCompletedEvent completed when completed.Data?.Cancelled == true => "cancelled",
                _ => "completed",
            };
            var applied = this.subAgentStates.Apply(new(child.SessionId.Value, CopilotSubAgentLifecycleLayer.Invocation,
                    state, sessionEvent.AgentId, InvocationToolCallId: toolCallId,
                    EventId: sessionEvent.Id == Guid.Empty ? null : sessionEvent.Id,
                    ParentEventId: sessionEvent.ParentId, ObservedAt: sessionEvent.Timestamp,
                    ExecutionBoundary: this.lifecycleGenerations.GetValueOrDefault(sessionEvent.Id) is > 0
                        ? this.lifecycleGenerations[sessionEvent.Id] : null));
            if (applied)
            {
                this.ProjectSubAgent(child, state, sessionEvent is SubagentFailedEvent failed
                    ? new AgentSubagentFailedException(failed.Data.Error) : null);
                if (state == "running" && child.AgentChat is { } resumed
                    && resumed.GetService(typeof(ICopilotSubAgentReceiver)) is CopilotSubAgentChatClient receiver
                    && receiver.BeginInvocation())
                    resumed.RequestHostedFollowUp();
            }
            if (sessionEvent is SubagentCompletedEvent or SubagentFailedEvent && sessionEvent.Id != Guid.Empty)
                this.acceptedTerminalGenerations[sessionEvent.Id] = applied
                    ? this.subAgentStates.InvocationGeneration(toolCallId) : null;
            this.LogLifecycleDecision(sessionEvent, applied ? "applied" : "stale-or-duplicate", child.SessionId.Value);
        }
    }

    private void LogLifecycleDecision(SessionEvent value, string decision, string? childKey = null)
    {
        var classification = CopilotSdkEventContract.Classify(value.Type);
        if (value is not SystemNotificationEvent && classification.Category is not ("lifecycle" or "reconcile" or "unsupported")) return;
        this.LogLifecycleDecisionCore("SDK", classification.Category, decision, childKey);
    }

    private void LogLifecycleDecisionCore(string source, string category, string decision, string? childKey = null)
    {
        var logger = this.loggerFactory?.CreateLogger<CopilotSdkChatClient>();
        if (logger?.IsEnabled(LogLevel.Debug) != true) return;
        lock (this.subAgentStateGate)
        {
            if (this.lifecycleDiagnosticCount++ >= 128) return;
            var correlation = 0;
            if (childKey is not null && !this.lifecycleDiagnosticCorrelations.TryGetValue(childKey, out correlation))
                this.lifecycleDiagnosticCorrelations[childKey] = correlation = this.lifecycleDiagnosticCorrelations.Count + 1;
            logger.LogDebug(
                "Copilot lifecycle source {Source}; category {Category}; child correlation {Correlation}; epoch {Epoch}; state {State}; decision {Decision}.",
                source, category, correlation, this.subAgentStates.Generation,
                childKey is null ? "uncorrelated" : this.subAgentStates.Project(childKey), decision);
        }
    }

    private void ObserveTaskNotification(SystemNotificationEvent notification)
    {
        var (taskId, state) = notification.Data?.Kind switch
        {
            SystemNotificationAgentIdle idle => (idle.AgentId, "idle"),
            SystemNotificationAgentCompleted completed => (completed.AgentId, completed.Status.Value),
            _ => (null, null),
        };
        if (taskId is null || state is null) return;
        lock (this.subAgentStateGate)
        {
            // Kind.AgentId is a task ID, never the envelope's child/session routing ID.
            if (!this.taskBindings.TryGetValue(taskId, out var binding)) return;
            var child = this.childrenByToolCallId.GetValueOrDefault(binding.Invocation);
            if (child is null || child.SessionId.Value != binding.ChildKey
                || !this.subAgentStates.CanFinishInvocation(binding.ChildKey, binding.Invocation, notification.Timestamp))
                return;
            var applied = this.subAgentStates.Apply(new(binding.ChildKey, CopilotSubAgentLifecycleLayer.Invocation,
                    state == "idle" ? "completed" : state, InvocationToolCallId: binding.Invocation,
                    EventId: notification.Id == Guid.Empty ? null : notification.Id,
                    ObservedAt: notification.Timestamp));
            if (applied)
                this.ProjectSubAgent(child, state);
            if (notification.Id != Guid.Empty)
                this.acceptedTerminalGenerations[notification.Id] = applied
                    ? this.subAgentStates.InvocationGeneration(binding.Invocation) : null;
        }
    }

    private async Task StartStateObserverAsync(ICopilotSession session, bool resumed, CancellationToken cancellationToken)
    {
        lock (this.subAgentStateGate)
        {
            foreach (var barrier in this.terminalObservationBarriers.Values) barrier.TrySetCanceled();
            this.terminalObservationBarriers.Clear();
            this.stateObserverFailure = null;
        }
        var channel = Channel.CreateUnbounded<SessionEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var shutdown = new CancellationTokenSource();
        long taskBoundary = 0;
        var reconciler = new CopilotSdkSubAgentStateReconciler(async ct =>
        {
            lock (this.subAgentStateGate) taskBoundary = this.subAgentStates.Generation;
            return await session.ListTasksAsync(ct).ConfigureAwait(false);
        }, snapshot =>
        {
            lock (this.subAgentStateGate)
            {
                if (taskBoundary != this.subAgentStates.Generation)
                {
                    _ = this.stateReconciler?.InvalidateAsync();
                    return;
                }
                this.ApplyTaskSnapshot(snapshot);
            }
        });
        this.stateObserverCancellation = shutdown;
        this.stateReconciler = reconciler;
        var rootEvents = new CopilotSdkRootEventQueue();
        rootEvents.ContinuationAvailable += () => this.RootContinuationAvailable?.Invoke();
        this.rootEventQueue = rootEvents;
        try
        {
            this.stateSubscription = session.Subscribe(e =>
            {
                channel.Writer.TryWrite(e);
                rootEvents.Accept(e);
            });
            if (resumed)
            {
                try
                {
                    foreach (var persisted in await this.ReadPersistedSubAgentEventsAsync(session.SessionId, cancellationToken).ConfigureAwait(false))
                        await reconciler.ObserveEventAsync(persisted,
                            _ => Task.FromResult<IReadOnlyList<SessionEvent>>([]),
                            this.ObserveSubAgentLifecycle).ConfigureAwait(false);
                }
                catch (NotSupportedException) { }
            }
            try { await reconciler.InvalidateAsync().WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (NotSupportedException)
            {
                // Older/remote sessions may not expose the experimental RPC yet.
            }
            try { await this.RefreshLiveSessionStatesAsync(cancellationToken).ConfigureAwait(false); }
            catch (NotSupportedException) { }
        }
        catch (Exception exception)
        {
            this.FailRootObservation(rootEvents, exception);
            channel.Writer.TryComplete();
            throw;
        }
        this.stateObserverPump = Task.Run(async () =>
        {
            try
            {
                await foreach (var e in channel.Reader.ReadAllAsync(shutdown.Token).ConfigureAwait(false))
                {
                    lock (this.subAgentStateGate)
                        if (this.lastObservedSdkTimestamp is null || e.Timestamp > this.lastObservedSdkTimestamp)
                            this.lastObservedSdkTimestamp = e.Timestamp;
                    // Even ephemeral invalidations participate in the predecessor chain.
                    bool unrepairedGap;
                    try
                    {
                        unrepairedGap = await reconciler.ObserveEventAsync(e,
                            ct => this.ReadPersistedSubAgentEventsAsync(session.SessionId, ct),
                            this.ObserveSubAgentLifecycle).ConfigureAwait(false);
                    }
                    catch (NotSupportedException)
                    {
                        this.ObserveSubAgentLifecycle(e);
                        unrepairedGap = true;
                    }
                    if (unrepairedGap || e is SessionBackgroundTasksChangedEvent or SessionIdleEvent
                        or AbortEvent or AgentInterruptedEvent
                        || CopilotSdkEventContract.Classify(e.Type).Category == "unsupported"
                        || e is SystemNotificationEvent { Data.Kind: SystemNotificationAgentIdle or SystemNotificationAgentCompleted })
                    {
                        try { await reconciler.InvalidateAsync().ConfigureAwait(false); }
                        catch (NotSupportedException) { }
                        try { await this.RefreshLiveSessionStatesAsync(shutdown.Token).ConfigureAwait(false); }
                        catch (NotSupportedException) { }
                    }
                    this.LogLifecycleDecision(e, unrepairedGap ? "gap-reconciled" : "predecessor-accepted");
                    await this.DispatchLiveChildUpdateAsync(e, shutdown.Token).ConfigureAwait(false);
                    if (e is SubagentCompletedEvent or SubagentFailedEvent && e.Id != Guid.Empty)
                    {
                        lock (this.subAgentStateGate)
                        {
                            if (!this.terminalObservationBarriers.TryGetValue(e.Id, out var barrier))
                                this.terminalObservationBarriers[e.Id] = barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            barrier.TrySetResult();
                        }
                    }
                    this.StateEventProcessed?.Invoke(e.Id);
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (Exception ex)
            {
                this.FailRootObservation(rootEvents, ex);
                channel.Writer.TryComplete();
                this.loggerFactory?.CreateLogger<CopilotSdkChatClient>()
                    .LogWarning(ex, "Observing Copilot background task state failed.");
            }
        });
        this.liveSessionPoller = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), this.StateObservationTimeProvider);
            try
            {
                while (await timer.WaitForNextTickAsync(shutdown.Token).ConfigureAwait(false))
                {
                    bool hasChildren;
                    lock (this.subAgentStateGate) hasChildren = this.childrenByAgentId.Count > 0;
                    if (hasChildren) await this.RefreshLiveSessionStatesAsync(shutdown.Token).ConfigureAwait(false);
                }
            }
            catch (NotSupportedException) { }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (Exception ex)
            {
                this.FailRootObservation(rootEvents, ex);
                channel.Writer.TryComplete();
                this.loggerFactory?.CreateLogger<CopilotSdkChatClient>()
                    .LogWarning(ex, "Observing Copilot live session status failed.");
            }
        });
    }

    private void FailRootObservation(CopilotSdkRootEventQueue? owner, Exception exception)
    {
        owner?.Fail(exception);
        lock (this.subAgentStateGate)
        {
            if (owner is null || !ReferenceEquals(owner, this.rootEventQueue)) return;
            this.stateObserverFailure ??= exception;
            foreach (var barrier in this.terminalObservationBarriers.Values) barrier.TrySetException(exception);
        }
    }

    private async Task DispatchLiveChildUpdateAsync(SessionEvent e, CancellationToken cancellationToken)
    {
        if (e is SystemNotificationEvent { Data.Kind: SystemNotificationAgentIdle or SystemNotificationAgentCompleted } notification)
        {
            this.FinishNotifiedChild(notification);
            return;
        }
        if (string.IsNullOrEmpty(e.AgentId) && e is not (SubagentCompletedEvent or SubagentFailedEvent)) return;
        if (this.currentRouter is not null && e is not (SubagentCompletedEvent or SubagentFailedEvent)) return;
        SubAgent? child;
        lock (this.subAgentStateGate)
            child = this.childrenByAgentId.GetValueOrDefault(e.AgentId ?? "")
                ?? (GetInvocationId(e) is { } invocation ? this.childrenByToolCallId.GetValueOrDefault(invocation) : null);
        if (child is null) return;
        if (e is SubagentCompletedEvent or SubagentFailedEvent)
        {
            Task? retire;
            lock (this.subAgentStateGate)
            {
                if (this.subAgentStates.IsActive(child.SessionId.Value)
                    || e.Id != Guid.Empty && (!this.acceptedTerminalGenerations.TryGetValue(e.Id, out var accepted)
                        || accepted is null || accepted != this.subAgentStates.InvocationGeneration(GetInvocationId(e)))
                    || !this.subAgentStates.CanFinishInvocation(child.SessionId.Value, GetInvocationId(e), e.Timestamp))
                    return;
                if (child.AgentChat?.GetService(typeof(ICopilotSubAgentReceiver)) is ICopilotSubAgentReceiver terminalReceiver)
                {
                    if (e is SubagentFailedEvent failed)
                        terminalReceiver.Fail(new AgentSubagentFailedException(failed.Data.Error));
                    else if (e is SubagentCompletedEvent { Data.Cancelled: true })
                        terminalReceiver.Fail(new OperationCanceledException("Sub-agent execution was cancelled."));
                    else terminalReceiver.Complete();
                }
                retire = child.AgentChat is { } terminalChat
                    ? this.currentRouter?.RetireChildAsync(terminalChat, () => this.CompleteTerminalObservation(e.Id))
                    : null;
            }
            this.OwnChildRetirement(retire, child);
            return;
        }
        var chat = child.AgentChat;
        if (chat is null && e is SubagentStartedEvent or SubagentConfiguredEvent
            or AssistantTurnStartEvent or SubagentCompletedEvent or SubagentFailedEvent) return;
        if (chat is null)
        {
            var lease = await child.AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
            chat = lease.LocalAgentChat;
            lock (this.subAgentStateGate) this.liveChildLeases[child.SessionId.Value] = lease;
        }
        if (chat.GetService(typeof(ICopilotSubAgentReceiver)) is not ICopilotSubAgentReceiver receiver) return;
        var events = Channel.CreateUnbounded<SessionEvent>();
        events.Writer.TryWrite(e);
        events.Writer.Complete();
        await foreach (var update in CopilotSdkStreamAdapter.TranslateCopilotSdkSessionEvents(events.Reader, cancellationToken)
                           .ConfigureAwait(false))
            if (update.Contents.Any(content => !CopilotSdkStreamAdapter.IsSubAgentLifecycleContent(content)))
            {
                if (receiver is CopilotSubAgentChatClient reusable && reusable.BeginInvocation())
                    chat.RequestHostedFollowUp();
                receiver.Push(update);
            }
    }

    private void FinishNotifiedChild(SystemNotificationEvent notification)
    {
        var taskId = notification.Data.Kind switch
        {
            SystemNotificationAgentIdle idle => idle.AgentId,
            SystemNotificationAgentCompleted completed => completed.AgentId,
            _ => null,
        };
        SubAgent? child;
        Task? retire;
        lock (this.subAgentStateGate)
        {
            if (taskId is null || !this.taskBindings.TryGetValue(taskId, out var binding)) return;
            child = this.childrenByToolCallId.GetValueOrDefault(binding.Invocation);
            if (child is null || child.SessionId.Value != binding.ChildKey
                || this.subAgentStates.IsActive(binding.ChildKey)
                || notification.Id != Guid.Empty && (!this.acceptedTerminalGenerations.TryGetValue(notification.Id, out var accepted)
                    || accepted is null || accepted != this.subAgentStates.InvocationGeneration(binding.Invocation))
                || !this.subAgentStates.CanFinishInvocation(binding.ChildKey, binding.Invocation, notification.Timestamp))
                return;
            var outcome = this.subAgentStates.Project(binding.ChildKey);
            // End the bound channel even during a parent turn; the envelope AgentId is unrelated.
            if (child.AgentChat?.GetService(typeof(ICopilotSubAgentReceiver)) is ICopilotSubAgentReceiver receiver)
            {
                if (outcome == "failed") receiver.Fail(new AgentSubagentFailedException("Sub-agent task failed."));
                else if (outcome == "cancelled") receiver.Fail(new OperationCanceledException("Sub-agent task was cancelled."));
                else receiver.Complete();
            }
            retire = child.AgentChat is { } chat ? this.currentRouter?.RetireChildAsync(chat) : null;
        }
        this.OwnChildRetirement(retire, child);
    }

    private void CompleteTerminalObservation(Guid eventId)
    {
        if (eventId == Guid.Empty) return;
        lock (this.subAgentStateGate)
        {
            if (!this.terminalObservationBarriers.TryGetValue(eventId, out var barrier))
                this.terminalObservationBarriers[eventId] = barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
            barrier.TrySetResult();
        }
    }

    private void OwnChildRetirement(Task? routingRetirement, SubAgent child)
    {
        var owner = this.rootEventQueue;
        RunningAgentChatLease? retainedLease;
        lock (this.subAgentStateGate) this.liveChildLeases.Remove(child.SessionId.Value, out retainedLease);
        var retirement = ObserveRetirementAsync();
        lock (this.subAgentStateGate)
        {
            this.childRetirementTasks.RemoveAll(task => task.IsCompletedSuccessfully);
            this.childRetirementTasks.Add(retirement);
        }

        async Task ObserveRetirementAsync()
        {
            try
            {
                await CompleteOwnedCleanupAsync(
                    () => routingRetirement ?? Task.CompletedTask,
                    () => retainedLease?.DisposeAsync().AsTask() ?? Task.CompletedTask).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                this.FailRootObservation(owner, exception);
                this.loggerFactory?.CreateLogger<CopilotSdkChatClient>()
                    .LogError(exception, "Retiring Copilot child resources failed.");
                throw;
            }
        }
    }

    private async Task<IReadOnlyList<SessionEvent>> ReadPersistedSubAgentEventsAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (this.copilotClient is null) return [];
        var events = new List<SessionEvent>();
        EventsReadResult page;
        do
        {
            page = await this.copilotClient.ReadPersistedEventsAsync(
                new ReadCopilotPersistedEventsRequest(sessionId, this.persistedEventCursor),
                cancellationToken).ConfigureAwait(false);
            events.AddRange(page.Events);
            if (page.Cursor == this.persistedEventCursor) break;
            this.persistedEventCursor = page.Cursor;
        } while (page.HasMore);
        return events;
    }

    private async Task StopStateObserverAsync()
    {
        await CompleteOwnedCleanupAsync(
            () => { this.rootEventQueue?.Stop(); return Task.CompletedTask; },
            () =>
            {
                this.stateSubscription?.Dispose();
                this.stateSubscription = null;
                this.rootEventQueue = null;
                this.stateObserverCancellation?.Cancel();
                this.stateReconciler?.Dispose();
                return Task.CompletedTask;
            },
            () => this.stateObserverPump ?? Task.CompletedTask,
            () => this.liveSessionPoller ?? Task.CompletedTask,
            async () => { await this.WaitForSteeringDeliveriesAsync().ConfigureAwait(false); },
            async () =>
            {
                Task[] sends;
                lock (this.admittedSends) sends = this.admittedSends.ToArray();
                await CompleteOwnedCleanupAsync(sends.Select<Task, Func<Task>>(task => () => task).ToArray()).ConfigureAwait(false);
            },
            async () =>
            {
                Task[] retirements;
                lock (this.subAgentStateGate) retirements = this.childRetirementTasks.ToArray();
                // Await separately so independently failing retirements retain their identities.
                await CompleteOwnedCleanupAsync(retirements.Select<Task, Func<Task>>(task => () => task).ToArray()).ConfigureAwait(false);
            },
            async () =>
            {
                RunningAgentChatLease[] leases;
                lock (this.subAgentStateGate)
                {
                    leases = this.liveChildLeases.Values.ToArray();
                    this.liveChildLeases.Clear();
                }
                await CompleteOwnedCleanupAsync(leases.Select<RunningAgentChatLease, Func<Task>>(
                    lease => () => lease.DisposeAsync().AsTask()).ToArray()).ConfigureAwait(false);
            },
            () =>
            {
                this.stateObserverPump = null;
                this.liveSessionPoller = null;
                this.stateObserverCancellation?.Dispose();
                this.stateObserverCancellation = null;
                this.stateReconciler = null;
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        lock (this.subAgentStateGate) this.childRetirementTasks.Clear();
    }

    private static async Task CompleteOwnedCleanupAsync(params Func<Task>[] cleanup)
    {
        List<Exception> failures = [];
        foreach (var action in cleanup)
        {
            try { await action().ConfigureAwait(false); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Copilot owned resource cleanup failed.", failures);
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var messageOptions = BuildMessageOptions(messages);
        await this.turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        CopilotSdkRootEventQueue? rootEvents = null;
        var ownsDirectResponse = false;
        try
        {
            var session = await this.EnsureSessionAsync(options, cancellationToken).ConfigureAwait(false);
            rootEvents = this.rootEventQueue!;
            rootEvents.BeginDirectResponse();
            ownsDirectResponse = true;
            var toolEventLock = new object();
            var directResponseComplete = false;
            var toolCalls = new List<FunctionCallContent>();
            var toolResults = new List<FunctionResultContent>();

            using var subscription = session.Subscribe(sessionEvent =>
            {
                switch (sessionEvent)
                {
                    case ToolExecutionStartEvent toolStart:
                        lock (toolEventLock)
                        {
                            if (!directResponseComplete) toolCalls.Add(CopilotToolEventMapper.MapToolStart(toolStart));
                        }

                        break;
                    case ToolExecutionCompleteEvent toolComplete:
                        lock (toolEventLock)
                        {
                            if (!directResponseComplete) toolResults.Add(CopilotToolEventMapper.MapToolComplete(toolComplete));
                        }

                        break;
                    case SessionIdleEvent idle when string.IsNullOrEmpty(idle.AgentId):
                    case SessionErrorEvent error when string.IsNullOrEmpty(error.AgentId):
                        lock (toolEventLock) directResponseComplete = true;
                        break;
                    default:
                        // Fix #1312 / #1323: mirror the streaming adapter's default arm — do not
                        // silently drop unmapped SDK event kinds, but log at Debug so we do not
                        // spam Warning per high-frequency SDK ping (e.g. AssistantStreamingDeltaEvent).
                        // This path does not have a transcript channel to emit content into, so
                        // surfacing is log-only.
                        if (sessionEvent is not null
                            && sessionEvent is not SessionIdleEvent
                            && sessionEvent is not SessionErrorEvent
                            && sessionEvent is not AssistantMessageDeltaEvent
                            && sessionEvent is not AssistantReasoningDeltaEvent
                            && sessionEvent is not AssistantUsageEvent
                            && sessionEvent is not SystemNotificationEvent
                            && sessionEvent is not SubagentStartedEvent
                            && sessionEvent is not SubagentCompletedEvent
                            && sessionEvent is not SubagentFailedEvent)
                        {
                            this.loggerFactory?.CreateLogger<CopilotSdkChatClient>().LogDebug(
                                "Copilot SDK non-streaming path received an unmapped session event of type {EventType} for AgentId {AgentId}.",
                                sessionEvent.GetType().FullName,
                                string.IsNullOrEmpty(sessionEvent.AgentId) ? "<root>" : sessionEvent.AgentId);
                        }

                        break;
                }
            });

            var finalEvent = await rootEvents.StartDirectResponse(() => session.SendAndWaitAsync(
                messageOptions,
                timeout: null,
                cancellationToken)).ConfigureAwait(false);

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
            if (ownsDirectResponse) rootEvents!.EndDirectResponse();
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

        var continuation = options?.AdditionalProperties?.TryGetValue(ConsumeContinuationOptionName, out var consume) == true
            && consume is true;
        var outputDrain = options?.AdditionalProperties?.GetValueOrDefault(CopilotSdkOutputDrain.OptionName) as CopilotSdkOutputDrain;
        var admittedBatch = options?.AdditionalProperties?.GetValueOrDefault(CopilotSdkRootBatch.OptionName) as CopilotSdkRootBatch;
        var messageOptions = continuation ? null : BuildMessageOptions(messages);
        var preparedSend = admittedBatch?.RequestSend;
        var promptSent = preparedSend is not null;
        Exception? preparedSendFailure = null;
        List<Exception> ownedFailures = [];
        ICopilotSession? failedSession = null;
        CopilotSdkRootEventQueue? failedRoot = null;
        var drainingFailure = false;
        var establishmentToken = admittedBatch?.HasAcceptedOutput == true
            || outputDrain is { StopRequested: true, HasOwner: true } ? CancellationToken.None : cancellationToken;
        await this.turnLock.WaitAsync(establishmentToken).ConfigureAwait(false);
        try
        {
            if (preparedSend is not null)
            {
                try { await preparedSend.ConfigureAwait(false); }
                catch (Exception failure)
                {
                    preparedSendFailure = failure;
                    admittedBatch!.Owner.SealAccepted();
                    RetainFailure(failure);
                    outputDrain?.RequestStop();
                }
            }
            do
            {
                var acquired = false;
                var updates = this.RunOwnedStreamingTurnAsync(async token =>
                {
                    var turn = await BeginTurnAsync(token).ConfigureAwait(false);
                    acquired = true;
                    return turn;
                }, cancellationToken, outputDrain).GetAsyncEnumerator();
                try
                {
                    while (true)
                    {
                        bool available;
                        try { available = await updates.MoveNextAsync().ConfigureAwait(false); }
                        catch (Exception failure)
                        {
                            RetainFailure(failure);
                            break;
                        }
                        if (!available) break;
                        yield return updates.Current;
                    }
                }
                finally
                {
                    try { await updates.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception failure) { RetainFailure(failure); }
                }
                if (drainingFailure)
                {
                    // Fail seals receipt, making this drain finite. Only already-accepted readers
                    // can enter another iteration; no prompt, steering, or native retry is eligible.
                    if (!acquired || failedRoot?.HasPending != true) break;
                    admittedBatch = new(failedRoot, failedRoot.Acquire(true), true);
                }
                else if (ownedFailures.Count > 0) break;
            } while (drainingFailure || (!continuation && !promptSent && outputDrain?.StopRequested != true)
                || (outputDrain?.StopRequested == true && this.rootEventQueue?.HasPending == true));
            if (preparedSendFailure is not null) AddOwnedFailure(ownedFailures, preparedSendFailure);
        }
        finally
        {
            try
            {
                if (drainingFailure && failedRoot?.HasPending == false && failedSession is not null)
                {
                    // This is an outer-owned callback, not an await of the iterator's
                    // own completion/finally. Its store and projection can run while
                    // the middleware awaits this MoveNext, before native retirement.
                    try { if (outputDrain is not null) await outputDrain.FlushAcceptedAsync().ConfigureAwait(false); }
                    catch (Exception failure) { AddOwnedFailure(ownedFailures, failure); }
                    try { await this.RetireFailedStreamingSessionAsync(failedSession).ConfigureAwait(false); }
                    catch (Exception failure) { AddOwnedFailure(ownedFailures, failure); }
                }
            }
            finally { this.turnLock.Release(); }
            ThrowOwnedFailures(ownedFailures);
        }

        void RetainFailure(Exception failure)
        {
            AddOwnedFailure(ownedFailures, failure);
            if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested
                && failedSession is null && this.rootEventQueue?.HasPending != true)
                return;
            failedSession ??= this.copilotSession;
            failedRoot ??= this.rootEventQueue;
            failedRoot?.Fail(failure);
            drainingFailure = true;
            outputDrain ??= new();
            outputDrain.RequestStop();
        }

        // The operation holds the turn lock through every accepted drain batch and final
        // retirement. Per-batch cleanup cannot invalidate the next old-session receipt.
        async Task<StreamingTurnContext> BeginTurnAsync(CancellationToken turnCancellationToken)
        {
            if (this.copilotSession is not null && this.rootEventQueue is { } existingRoot)
            {
                // Fence the old configuration, then drain every accepted batch before replacing it.
                if (!continuation && this.currentSessionSignature != ComputeSessionSignatureCore(options, this.workingDirectoryOverride))
                    existingRoot.SealAccepted();
                if (existingRoot.IsSealed)
                {
                    if (admittedBatch is { HasAcceptedOutput: false })
                    {
                        admittedBatch.Dispose();
                        admittedBatch = null;
                    }
                    if (admittedBatch is null && existingRoot.HasPending)
                        admittedBatch = new(existingRoot, existingRoot.Acquire(true, out var acquired), acquired);
                }
            }
            if (admittedBatch?.HasAcceptedOutput != true)
                cancellationToken.ThrowIfCancellationRequested();
            var session = admittedBatch?.HasAcceptedOutput == true
                ? (drainingFailure ? failedSession : this.copilotSession)
                    ?? throw new InvalidOperationException("The accepted root batch has no owning native session.")
                : await this.EnsureSessionAsync(options, turnCancellationToken).ConfigureAwait(false);
            if (admittedBatch?.HasAcceptedOutput != true && this.rootEventQueue?.HasPending != true)
                lock (this.subAgentStateGate)
                    if (this.stateObserverFailure is { } failure)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

            var channel = Channel.CreateUnbounded<ChatResponseUpdate>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });

            var router = new CopilotSubAgentRouter(
                channel.Writer,
                this.runningAgentChatFactory,
                this.subAgentTable,
                this.loggerFactory?.CreateLogger<CopilotSubAgentRouter>(),
                this.RegisterSubAgent,
                agentId =>
                {
                    lock (this.subAgentStateGate) return this.childrenByAgentId.GetValueOrDefault(agentId);
                },
                toolCallId =>
                {
                    lock (this.subAgentStateGate) return this.childrenByToolCallId.GetValueOrDefault(toolCallId);
                },
                this.ReprojectAfterInvocation,
                async result =>
                {
                    var eventIdValue = result.AdditionalProperties?.GetValueOrDefault(CopilotSdkStreamAdapter.LifecycleEventIdPropertyName);
                    var eventId = eventIdValue is Guid id ? id
                        : eventIdValue is JsonElement { ValueKind: JsonValueKind.String } element && element.TryGetGuid(out var parsedId)
                            ? parsedId : Guid.Empty;
                    if (eventId != Guid.Empty)
                    {
                        Task observed;
                        lock (this.subAgentStateGate)
                        {
                            if (this.stateObserverFailure is { } failure)
                                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                            if (!this.terminalObservationBarriers.TryGetValue(eventId, out var barrier))
                                this.terminalObservationBarriers[eventId] = barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            observed = barrier.Task;
                        }
                        await observed.WaitAsync(outputDrain is null ? turnCancellationToken : CancellationToken.None).ConfigureAwait(false);
                    }
                    lock (this.subAgentStateGate)
                    {
                        var child = this.childrenByAgentId.GetValueOrDefault(result.CallId)
                            ?? this.childrenByToolCallId.GetValueOrDefault(result.CallId);
                        if (child is null) return true;
                        var invocationValue = result.AdditionalProperties?.GetValueOrDefault(
                            CopilotSdkStreamAdapter.InvocationToolCallIdPropertyName);
                        var invocation = invocationValue as string
                            ?? (invocationValue is JsonElement { ValueKind: JsonValueKind.String } text ? text.GetString() : null);
                        var timestampValue = result.AdditionalProperties?.GetValueOrDefault(
                            CopilotSdkStreamAdapter.LifecycleTimestampPropertyName);
                        DateTimeOffset? timestamp = timestampValue switch
                        {
                            DateTimeOffset date => date,
                            JsonElement { ValueKind: JsonValueKind.String } json when json.TryGetDateTimeOffset(out var parsed) => parsed,
                            _ => null,
                        };
                        return (eventId == Guid.Empty || this.acceptedTerminalGenerations.TryGetValue(eventId, out var accepted)
                            && accepted is not null && accepted == this.subAgentStates.InvocationGeneration(invocation))
                            && !this.subAgentStates.IsActive(child.SessionId.Value)
                            && this.subAgentStates.CanFinishInvocation(child.SessionId.Value, invocation, timestamp);
                    }
                }, canonicalLifecycle: true);
            this.currentRouter = router;

            // Fix for GitHub issue #765: serialize event dispatch via a channel to prevent concurrent
            // routing calls from corrupting internal dictionaries. SingleReader ensures the drain
            // loop is the only consumer; SingleWriter=false allows multiple SDK event callbacks to write.
            var rootEvents = (drainingFailure ? failedRoot : this.rootEventQueue)
                ?? throw new InvalidOperationException("Copilot session has no root event receipt queue.");
            var acquiredContinuation = admittedBatch?.IsContinuation ?? false;
            var eventReader = admittedBatch?.Reader;
            if (admittedBatch is not null && !ReferenceEquals(admittedBatch.Owner, rootEvents))
                throw new InvalidOperationException("The admitted Copilot batch belongs to a different session receipt queue.");
            if (admittedBatch is not null) eventReader = admittedBatch.Take();
            eventReader ??= rootEvents.Acquire(continuation, out acquiredContinuation);
            admittedBatch = null;
            outputDrain?.Attach(rootEvents.SealAccepted, rootEvents.Fail);

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
                        .TranslateCopilotSdkSessionEvents(
                            eventReader,
                            this.loggerFactory?.CreateLogger(typeof(CopilotSdkStreamAdapter).FullName!),
                            rootEvents.TranslationFor(eventReader),
                            outputDrain is null ? turnCancellationToken : CancellationToken.None)
                        .ConfigureAwait(false))
                    {
                        await router.RouteAsync(update).ConfigureAwait(false);
                    }

                    rootEvents.CompleteDispatch(error => channel.Writer.TryComplete(error));
                }
                catch (OperationCanceledException) when (outputDrain is null && turnCancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    this.FailRootObservation(rootEvents, exception);
                    // Translation consumed the failing event, not the unread suffix. Return only
                    // that suffix ahead of later batches, before exposing the routed failure.
                    if (eventReader.TryPeek(out _)) rootEvents.ReturnUnconsumed(eventReader, true);
                    // Without this, a dispatch failure kills the event loop while the CLI keeps
                    // streaming: every subsequent session event is dropped and the turn's channel
                    // never completes, so the remaining output silently never renders (issue
                    // #912). Fail the turn loudly instead.
                    this.loggerFactory?.CreateLogger<CopilotSdkChatClient>().LogError(
                        exception,
                        "Session event dispatch failed; failing the turn.");
                    rootEvents.CompleteDispatch(error => channel.Writer.TryComplete(error), exception);
                }
            }, outputDrain is null ? turnCancellationToken : CancellationToken.None);

            // Reset the teardown gate for this turn: a previous turn's cancellation may have
            // left steeringSuspended=true (issue #1142). The new turn must start unsuspended so
            // its OnQueueChanged forwards Immediate items to the live session.
            if (!drainingFailure) this.steeringSuspended = false;

            // While a turn is running, forward any Immediate-immediacy queue items as steering
            // input. SendAsync with Mode="immediate" is safe to call concurrently with a live turn.
            // The subscription (and dispatch to ForwardPendingImmediateMessages) is established
            // through SubscribeImmediateQueueSteering so that fake-BeginTurnAsync test call sites
            // exercise the exact same production wiring — not a duplicated copy — when observing
            // SteeringMessageForwarded during a live turn.
            IDisposable queueSubscription = new NoopDisposable();
            bool SteeringAdmissionOpen() => !this.steeringSuspended
                && !cancellationToken.IsCancellationRequested && outputDrain?.StopRequested != true;
            void InstallSteering() => queueSubscription = this.SubscribeImmediateQueueSteering(
                (immediate, _) => rootEvents.StartSteering(eventReader, SteeringAdmissionOpen,
                    () => session.SendAsync(immediate, cancellationToken)),
                () => SteeringAdmissionOpen() && rootEvents.IsReceiving(eventReader));
            if (promptSent || continuation || acquiredContinuation) InstallSteering();
            var requestStarted = false;
            var requestReceiptVersion = rootEvents.ReceiptVersion;

            var subscription = new AsyncDelegateDisposable(async () =>
            {
                this.steeringSuspended = true;
                queueSubscription.Dispose();
                await this.WaitForSteeringDeliveriesAsync().ConfigureAwait(false);
                try
                {
                    rootEvents.Release(eventReader);
                }
                finally
                {
                    try
                    {
                        await dispatchLoop.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (turnCancellationToken.IsCancellationRequested)
                    {
                    }
                    finally
                    {
                        try
                        {
                            await router.DisposeRemainingLeasesAsync().ConfigureAwait(false);
                        }
                        finally
                        {
                            if (ReferenceEquals(this.currentRouter, router)) this.currentRouter = null;
                        }
                    }
                }
            });

            return new StreamingTurnContext(
                channel.Reader,
                subscription,
                async _ =>
                {
                    if (continuation || acquiredContinuation || promptSent) return;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!rootEvents.TryStartRequest(eventReader,
                        () =>
                        {
                            requestStarted = true;
                            requestReceiptVersion = rootEvents.ReceiptVersion;
                            return session.SendAsync(messageOptions!, cancellationToken);
                        }, out var sending,
                        () => cancellationToken.ThrowIfCancellationRequested()))
                    {
                        acquiredContinuation = true;
                        return;
                    }
                    await sending!.ConfigureAwait(false);
                    promptSent = true;
                    InstallSteering();
                },
                () =>
                {
                    if (rootEvents.HasFailure)
                    {
                        failedSession = session;
                        failedRoot = rootEvents;
                        return Task.CompletedTask;
                    }
                    return drainingFailure || outputDrain?.StopRequested == true && rootEvents.HasPending
                        ? Task.CompletedTask
                        : this.AbortAndInvalidateSessionAsync(session, router);
                },
                () =>
                {
                    if (rootEvents.IsSealed || rootEvents.HasPending)
                    {
                        failedSession = session;
                        failedRoot = rootEvents;
                    }
                    else this.InvalidateBrokenSession(session);
                    return Task.CompletedTask;
                },
                failure =>
                {
                    if (!requestStarted || rootEvents.ReceiptVersion == requestReceiptVersion) return false;
                    this.FailRootObservation(rootEvents, failure);
                    RetainFailure(failure);
                    return true;
                });
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
        [EnumeratorCancellation] CancellationToken cancellationToken,
        CopilotSdkOutputDrain? outputDrain = null)
    {
        ArgumentNullException.ThrowIfNull(beginTurnAsync);

        var establishmentToken = outputDrain is { StopRequested: true, HasOwner: true } ? CancellationToken.None : cancellationToken;
        await this.turnLock.WaitAsync(establishmentToken).ConfigureAwait(false);
        try
        {
            await foreach (var update in this.RunOwnedStreamingTurnAsync(beginTurnAsync, cancellationToken, outputDrain).ConfigureAwait(false))
                yield return update;
        }
        finally
        {
            this.turnLock.Release();
        }
    }

    private async IAsyncEnumerable<ChatResponseUpdate> RunOwnedStreamingTurnAsync(
        Func<CancellationToken, Task<StreamingTurnContext>> beginTurnAsync,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        CopilotSdkOutputDrain? outputDrain)
    {
        var establishmentToken = outputDrain is { StopRequested: true, HasOwner: true } ? CancellationToken.None : cancellationToken;
        var turn = await this.GetReadyTurnAsync(beginTurnAsync, establishmentToken).ConfigureAwait(false);
        var readCancellationToken = outputDrain is null ? cancellationToken : CancellationToken.None;
        var updates = turn.Reader.ReadAllAsync(readCancellationToken).GetAsyncEnumerator(readCancellationToken);
        List<Exception> failures = [];
        try
        {
            while (true)
            {
                bool available;
                try { available = await MoveNextOrInvalidateAsync(updates, turn, cancellationToken).ConfigureAwait(false); }
                catch (Exception failure)
                {
                    AddOwnedFailure(failures, failure);
                    break;
                }
                if (!available) break;
                yield return updates.Current;
            }
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested || outputDrain?.StopRequested == true)
            {
                try { await turn.OnCancelledAsync().ConfigureAwait(false); }
                catch (Exception failure) { AddOwnedFailure(failures, failure); }
            }
            try { await updates.DisposeAsync().ConfigureAwait(false); }
            catch (Exception failure) { AddOwnedFailure(failures, failure); }
            try { await turn.Subscription.DisposeAsync().ConfigureAwait(false); }
            catch (Exception failure) { AddOwnedFailure(failures, failure); }
            ThrowOwnedFailures(failures);
        }
    }

    private static void AddOwnedFailure(List<Exception> failures, Exception failure)
    {
        if (failures.Any(existing => Contains(existing, failure))) return;
        if (failures.Count > 0 && failure is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions) AddOwnedFailure(failures, inner);
        }
        else if (failure.InnerException is { } cause && failures.Any(existing => Contains(failure, existing)))
            AddOwnedFailure(failures, cause);
        else failures.Add(failure);

        static bool Contains(Exception outer, Exception inner) => ReferenceEquals(outer, inner)
            || (outer is AggregateException aggregate
                ? aggregate.InnerExceptions.Any(value => Contains(value, inner))
                : outer.InnerException is { } cause && Contains(cause, inner));
    }

    private static void ThrowOwnedFailures(List<Exception> failures)
    {
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Copilot streaming and owned cleanup failed.", failures);
    }

    private async Task RetireFailedStreamingSessionAsync(ICopilotSession session)
    {
        if (Interlocked.CompareExchange(ref this.copilotSession, null, session) != session) return;
        this.pendingResumeSessionId = null;
        this.currentSessionSignature = null;
        await CompleteOwnedCleanupAsync(
            this.StopStateObserverAsync,
            () => session.DisposeAsync().AsTask()).ConfigureAwait(false);
    }

    private static async Task<bool> MoveNextOrInvalidateAsync(
        IAsyncEnumerator<ChatResponseUpdate> updates,
        StreamingTurnContext turn,
        CancellationToken cancellationToken)
    {
        try
        {
            return await updates.MoveNextAsync().ConfigureAwait(false);
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            // A terminal transport/session failure makes this SDK session unusable. Drop it before
            // releasing the turn lock so a queued retry opens a fresh remote channel instead of
            // reusing the failed session (#1594).
            try { await turn.OnPipeBrokenAsync().ConfigureAwait(false); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Streaming read and pipe cleanup failed.", failure, cleanupFailure);
            }
            throw;
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
            try
            {
                await turn.SendAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (System.IO.IOException sendFailure) when (!cancellationToken.IsCancellationRequested)
            {
                if (turn.RetainAcceptedSendFailure?.Invoke(sendFailure) == true) return turn;
                // Only a broken pipe retries; credential refusals keep their original required action.
                await turn.Subscription.DisposeAsync().ConfigureAwait(false);
                await turn.OnPipeBrokenAsync().ConfigureAwait(false);
                turn = await beginTurnAsync(cancellationToken).ConfigureAwait(false);
                await turn.SendAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception sendFailure)
        {
            if (turn.RetainAcceptedSendFailure?.Invoke(sendFailure) == true) return turn;
            try
            {
                await turn.Subscription.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Streaming turn startup and cleanup failed.",
                    sendFailure,
                    cleanupFailure);
            }
            throw;
        }

        return turn;
    }

    // Actually stops the in-flight Copilot CLI turn. Cancelling the read loop alone leaves the CLI
    // generating and lets a stale SessionIdleEvent from the abandoned turn complete the next turn's
    // channel (a silent empty response), so the session is also invalidated and recreated next turn.
    private async Task AbortAndInvalidateSessionAsync(ICopilotSession session, CopilotSubAgentRouter? router = null)
    {
        // Suspend steering BEFORE aborting/invalidating the session (GitHub issue #1142). Once
        // this flag is set, OnQueueChanged early-returns without calling TryDequeueNextImmediate,
        // so a user message enqueued while teardown runs is neither routed to the dying session
        // (which would produce a "Session not found" fault whose unobserved Task crashed the
        // finalizer thread) nor dequeued-and-dropped. Leaving the Immediate item in place lets
        // AgentChat's processing loop promote it to a fresh turn on the recovered session.
        this.steeringSuspended = true;

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

        // Fix #1193: force any sub-agents whose SubagentCompleted/SubagentFailed events the
        // aborted session will never deliver into a terminal (Failed) state. Idempotent with any
        // late real events via SetCompletionState's equality guard and the router's dictionary
        // clear. Router may be null when a legacy caller stubs OnCancelledAsync directly.
        if (router is not null)
        {
            try
            {
                await router.TerminalizeRemainingChildrenAsync(
                        new OperationCanceledException("Parent Copilot chat was interrupted."))
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                this.loggerFactory?.CreateLogger<CopilotSdkChatClient>()
                    .LogDebug(exception, "Terminalizing running sub-agents on parent interrupt failed.");
            }
        }
    }

    /// <summary>
    /// Subscribes the client's queue manager (if any) so that any Immediate-immediacy queue
    /// items are dequeued and forwarded to <paramref name="sendAsync"/> via
    /// <see cref="ForwardPendingImmediateMessages"/>. Returns an <see cref="IDisposable"/> that
    /// unsubscribes when disposed. Extracted from <c>BeginTurnAsync</c> so tests can exercise
    /// the exact production wiring that raises <see cref="SteeringMessageForwarded"/> during a
    /// live turn instead of reproducing the wiring themselves.
    /// </summary>
    internal IDisposable SubscribeImmediateQueueSteering(
        Func<MessageOptions, CancellationToken, Task> sendAsync,
        Func<bool>? admissionOpen = null)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);

        var queueManager = this.queueManager;
        if (queueManager is null)
        {
            return new NoopDisposable();
        }

        var closed = false;
        var draining = false;
        void CatchUp() => queueManager.Mutate(() =>
        {
            if (closed || draining) return false;
            draining = true;
            try { this.ForwardPendingImmediateMessagesCore(sendAsync, admissionOpen); }
            finally { draining = false; }
            return true;
        });
        void Handler(object? sender, AgentInputQueueManager.QueueStateChangedEventArgs e)
        {
            if (e.ChangeKind == AgentInputQueueManager.QueueStateChangeKind.ItemAdded) CatchUp();
        }

        void Close() => queueManager.Mutate(() =>
        {
            closed = true;
            queueManager.QueueStateChanged -= Handler;
            return true;
        });
        queueManager.QueueStateChanged += Handler;
        // The queue's existing claim/mutation gate serializes catch-up with ItemAdded.
        // Reentrant additions are consumed by this same drain, never a nested sender.
        try { CatchUp(); }
        catch { Close(); throw; }
        return new SyncDelegateDisposable(Close);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class SyncDelegateDisposable(Action dispose) : IDisposable
    {
        private Action? dispose = dispose ?? throw new ArgumentNullException(nameof(dispose));

        public void Dispose()
        {
            var d = Interlocked.Exchange(ref this.dispose, null);
            d?.Invoke();
        }
    }

    /// <summary>
    /// Body of the per-turn <c>OnQueueChanged</c> subscription. Split out so tests can exercise
    /// the gating and fault-observation behaviour without a real Copilot CLI session.
    /// GitHub issue #1142.
    /// </summary>
    internal void ForwardPendingImmediateMessages(
        Func<MessageOptions, CancellationToken, Task> sendAsync,
        AgentInputQueueManager.QueueStateChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);
        ArgumentNullException.ThrowIfNull(e);

        // Do NOT dequeue during teardown (steeringSuspended) — the Immediate item must remain in
        // the queue so AgentChat's processing loop can promote it to a fresh turn on the
        // recovered session (issue #1142). Also ignore non-additions.
        if (this.steeringSuspended || e.ChangeKind != AgentInputQueueManager.QueueStateChangeKind.ItemAdded)
        {
            return;
        }

        this.ForwardPendingImmediateMessagesCore(sendAsync);
    }

    private void ForwardPendingImmediateMessagesCore(
        Func<MessageOptions, CancellationToken, Task> sendAsync,
        Func<bool>? admissionOpen = null)
    {
        while (!this.steeringSuspended && admissionOpen?.Invoke() != false
            && this.queueManager!.TryDequeueNextImmediate(out var item))
        {
            var deliveries = new List<Task<SteeringDeliveryOutcome>>();
            foreach (var message in item.Messages ?? [])
            {
                var immediateOptions = BuildImmediateMessageOptions(message);
                if (immediateOptions is null)
                {
                    continue;
                }

                var delivery = this.DeliverSteeringAsync(message, ct =>
                {
                    // This claimed attempt owns both notification and truthful delivery failure.
                    this.SteeringMessageForwarded?.Invoke(message);
                    return sendAsync(immediateOptions, ct);
                });
                deliveries.Add(delivery);
                lock (this.steeringDeliveries) this.steeringDeliveries.Add(delivery);
            }
            if (item.TurnCompletion is { } completion)
            {
                completion.ClaimTerminal();
                completion.CompleteAfter(RequireDeliveryAsync(deliveries));
            }
        }

        static async Task RequireDeliveryAsync(List<Task<SteeringDeliveryOutcome>> deliveries)
        {
            var outcomes = await Task.WhenAll(deliveries).ConfigureAwait(false);
            var failures = outcomes.Where(outcome => !outcome.Delivered).Select(outcome => outcome.Failure!).ToArray();
            if (failures.Length == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Length > 1) throw new AggregateException("Steering input was not delivered.", failures);
        }
    }

    private async Task<SteeringDeliveryOutcome> DeliverSteeringAsync(
        ChatMessage message, Func<CancellationToken, Task> sendAsync)
    {
        try
        {
            await ForwardSteeringAsync(sendAsync, this.loggerFactory?.CreateLogger<CopilotSdkChatClient>()).ConfigureAwait(false);
            return new(message, null);
        }
        catch (Exception failure)
        {
            var failures = new List<Exception> { failure };
            foreach (var handler in this.SteeringDeliveryFailed?.GetInvocationList()
                         .Cast<Func<ChatMessage, Exception, Task>>() ?? [])
            {
                try { await handler(message, failure).ConfigureAwait(false); }
                catch (Exception notificationFailure) { failures.Add(notificationFailure); }
            }
            return new(message, failures.Count == 1 ? failure : new AggregateException(failures));
        }
    }

    internal static async Task ForwardSteeringAsync(
        Func<CancellationToken, Task> sendAsync,
        ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(sendAsync);

        await sendAsync(CancellationToken.None).ConfigureAwait(false);
    }

    // Drops the cached session (disposing it in the background) so the next turn creates a fresh one.
    private void InvalidateCopilotSession(ICopilotSession session)
    {
        if (Interlocked.CompareExchange(ref this.copilotSession, null, session) != session)
        {
            return;
        }
        this.stateSubscription?.Dispose();
        this.stateObserverCancellation?.Cancel();
        this.stateReconciler?.Dispose();

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
    private void InvalidateBrokenSession(ICopilotSession session)
    {
        if (Interlocked.CompareExchange(ref this.copilotSession, null, session) != session)
        {
            return;
        }
        this.stateSubscription?.Dispose();
        this.stateObserverCancellation?.Cancel();
        this.stateReconciler?.Dispose();

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

    event Action<string>? ICopilotSdkSessionSink.SessionEstablished
    {
        add => this.SessionEstablished += value;
        remove => this.SessionEstablished -= value;
    }

    void ICopilotSdkSessionSink.SetResumeSessionId(string? sessionId) => this.SetResumeSessionId(sessionId);

    /// <inheritdoc />
    public void Dispose()
    {
        // Never block a GUI caller. Observe the background fault here, while retaining the
        // original faulted task so a later DisposeAsync still reports the cleanup failure.
        _ = ObserveSynchronousDisposalAsync();

        async Task ObserveSynchronousDisposalAsync()
        {
            try { await this.GetDisposalTask().ConfigureAwait(false); }
            catch (Exception failure)
            {
                this.loggerFactory?.CreateLogger<CopilotSdkChatClient>()
                    .LogError(failure, "Disposing Copilot resources failed; asynchronous disposal retains the failure.");
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => new(this.GetDisposalTask());

    private Task GetDisposalTask()
    {
        lock (this.disposalGate) return this.disposalTask ??= Task.Run(this.DisposeCoreAsync);
    }

    private async Task DisposeCoreAsync()
    {
        await CompleteOwnedCleanupAsync(
            this.StopStateObserverAsync,
            () => this.copilotSession?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            () => this.copilotClient?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            () => this.DisposeRuntimeConnectionSelectionAsync().AsTask(),
            () =>
            {
                this.sessionInitializationLock.Dispose();
                this.turnLock.Dispose();
                return Task.CompletedTask;
            },
            () => this.credentialOwner?.DisposeAsync().AsTask() ?? Task.CompletedTask,
            () => this.ownedSecretResolver?.DisposeAsync().AsTask() ?? Task.CompletedTask).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a <see cref="MessageOptions"/> from the message history by collecting the current
    /// turn's user-message batch (the trailing run of user messages that follows a non-user
    /// separator) and combining their text content and data attachments into a single prompt.
    /// When multiple messages are queued in one turn their texts are joined with
    /// <c>\n\n---\n\n</c> so every queued message is visible to the model.
    /// <para>
    /// Defense-in-depth against issue #1104: if the history degenerates into a run of consecutive
    /// user messages with no non-user separator (e.g. an errored/cancelled/restored turn whose
    /// assistant reply was never persisted), the backward walk cannot distinguish the current
    /// turn's batch from earlier turns. In that case the merge is bounded to the last user
    /// message only, so a degenerate user-only history cannot collapse into one giant prompt
    /// that re-answers stale user turns.
    /// </para>
    /// </summary>
    internal static MessageOptions BuildMessageOptions(IEnumerable<ChatMessage> messages)
    {
        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        // Collect consecutive trailing user messages — these are the batched messages for this turn.
        // Stop as soon as a non-user message is encountered so earlier historical turns are not included.
        var batchMessages = new List<ChatMessage>();
        var foundSeparator = false;
        for (var index = materialized.Count - 1; index >= 0; index--)
        {
            var message = materialized[index];
            if (message.Role != ChatRole.User)
            {
                foundSeparator = true;
                break;
            }

            batchMessages.Add(message);
        }

        batchMessages.Reverse();

        // Defense-in-depth (issue #1104): when the trailing user run spans the entire history
        // with no non-user separator, we cannot reliably identify the current turn's batch and
        // must not concatenate historical user turns into one prompt. Keep only the last user
        // message — that is the caller's most recent input and is safe to send. Genuinely
        // batched user messages within one turn are still merged when a preceding non-user
        // (assistant/system/tool) message is present, matching the stateful-session model
        // documented in docs/design/github-copilot-provider-support.md.
        if (!foundSeparator && batchMessages.Count > 1)
        {
            batchMessages = new List<ChatMessage> { batchMessages[^1] };
        }

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

            AddDataContentAttachments(options, dataItems);

            return options;
        }

        var lastWithText = materialized.LastOrDefault(message => !string.IsNullOrEmpty(message.Text));
        return new MessageOptions { Prompt = lastWithText?.Text ?? string.Empty };
    }

    internal static MessageOptions? BuildImmediateMessageOptions(ChatMessage message)
    {
        var text = string.Concat(
            message.Contents.OfType<TextContent>().Select(content => content.Text));
        var dataItems = message.Contents.OfType<DataContent>().ToList();

        if (string.IsNullOrWhiteSpace(text) && dataItems.Count == 0)
        {
            return null;
        }

        var options = new MessageOptions { Prompt = text, Mode = "immediate" };
        AddDataContentAttachments(options, dataItems);
        return options;
    }

    private static void AddDataContentAttachments(MessageOptions options, IReadOnlyCollection<DataContent> dataItems)
    {
        if (dataItems.Count == 0)
        {
            return;
        }

        options.Attachments = dataItems
            .Select(static d => (Attachment)new AttachmentBlob
            {
                Data = Convert.ToBase64String(d.Data.ToArray()),
                MimeType = d.MediaType ?? string.Empty,
                DisplayName = d.MediaType ?? "attachment",
            })
            .ToList();
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

    private async Task<ICopilotSession> EnsureSessionAsync(ChatOptions? options, CancellationToken cancellationToken)
    {
        var signature = ComputeSessionSignatureCore(options, this.workingDirectoryOverride);

        if (this.copilotSession is { } existingSession && this.currentSessionSignature == signature
            && this.rootEventQueue is { } existingRoot && (!existingRoot.IsSealed || existingRoot.HasPending))
        {
            return existingSession;
        }

        await this.sessionInitializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (this.copilotSession is { } alreadyInitialized && this.currentSessionSignature == signature
                && this.rootEventQueue is { } initializedRoot && (!initializedRoot.IsSealed || initializedRoot.HasPending))
            {
                return alreadyInitialized;
            }

            // The Copilot CLI captures the tool set (and other session config) when the session is
            // created, so a change to the effective tools/instructions/reasoning between turns only
            // takes effect by recreating the session. The CLI client is reused across recreations.
            if (this.copilotClient is null)
            {
                // Issue #1443: when the model's resolved executor descriptor is non-local, obtain the
                // innermost SDK session over a transport; the router and context providers stay local.
                var remoteClient = await this.TryCreateRemoteClientAsync(cancellationToken).ConfigureAwait(false);
                if (remoteClient is not null)
                {
                    await StartClientAsync(remoteClient, cancellationToken).ConfigureAwait(false);
                    this.copilotClient = remoteClient;
                }
                else
                {
                    var workingDirectory = GetWorkingDirectory(options);
                    var client = await this.CreateClientAsync(
                        workingDirectory,
                        cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await StartClientAsync(client, cancellationToken).ConfigureAwait(false);
                        await this.UpsertAccountAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        try
                        {
                            await client.DisposeAsync().ConfigureAwait(false);
                        }
                        finally
                        {
                            await DisposeRuntimeConnectionSelectionAsync().ConfigureAwait(false);
                        }
                        throw;
                    }
                    this.copilotClient = client;
                }
            }

            if (this.copilotClient is CredentialBoundCopilotClient)
            {
                await this.UpsertAccountAsync(cancellationToken).ConfigureAwait(false);
            }

            if (this.copilotSession is { } staleSession)
            {
                var retiringRoot = this.rootEventQueue;
                retiringRoot?.SealAccepted();
                if (retiringRoot?.HasPending == true)
                    throw new InvalidOperationException("Accepted Copilot root continuations must be consumed before session replacement.");
                // Preserve the existing session id so the next CreateOrResumeSessionAsync call
                // resumes the Copilot CLI session with the updated config (e.g. new working
                // directory) rather than creating a blank new session. Model changes no longer
                // reach this teardown path: SetModelIdAsync retunes the live session in place via
                // CopilotSession.SetModelAsync, preserving the conversation history (issue #1418).
                this.pendingResumeSessionId ??= staleSession.SessionId;

                await this.StopStateObserverAsync().ConfigureAwait(false);
                await staleSession.DisposeAsync().ConfigureAwait(false);
                this.copilotSession = null;
                this.currentSessionSignature = null;
            }

            var resuming = this.pendingResumeSessionId is not null;
            var session = await this.CreateOrResumeSessionAsync(options, cancellationToken).ConfigureAwait(false);

            this.copilotSession = session;
            this.currentSessionSignature = signature;
            await this.StopStateObserverAsync().ConfigureAwait(false);
            await this.StartStateObserverAsync(session, resuming, cancellationToken).ConfigureAwait(false);
            this.SessionEstablished?.Invoke(session.SessionId);
            return session;
        }
        finally
        {
            this.sessionInitializationLock.Release();
        }
    }

    private async Task UpsertAccountAsync(CancellationToken cancellationToken)
    {
        if (this.accountUpsertService is null || this.accountUpsertCompleted)
        {
            return;
        }
        if (this.accessBroker is null)
        {
            throw new AgentCredentialAccessException();
        }

        var request = GitHubCredentialAccessRequest.Create(
            GitHubCredentialPurpose.Agent,
            this.credentialRequestor ?? new AccessRequestor
            {
                Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Agent, "copilot-account-upsert"),
                Kind = AccessRequestorKind.Agent,
                SafeDisplayName = "Agent account",
            },
            this.credentialLineage,
            this.credentialUse is null
                ? "copilot-account-upsert"
                : $"{this.credentialUse.Fingerprint.Digest}:copilot-account-upsert",
            this.accessPresentationScope());
        if (this.credentialUse is { } parent)
        {
            request = request with { Use = request.Use with { MemoryScopes = parent.MemoryScopes } };
        }
        var outcome = await this.accessBroker.AcquireAsync(request, cancellationToken).ConfigureAwait(false);
        await using var result = outcome switch
        {
            AccessSucceeded<GitHubCredentialAccessResult> succeeded => succeeded.Result,
            AccessCancelled<GitHubCredentialAccessResult> => throw new OperationCanceledException(cancellationToken),
            AccessAuthorizationRequired<GitHubCredentialAccessResult> required => throw new AgentCredentialAccessException(
                CredentialLeaseRequiredActionKind.Authorization, required.RequestIdentifier),
            AccessReacquisitionRequired<GitHubCredentialAccessResult> required => throw new AgentCredentialAccessException(
                CredentialLeaseRequiredActionKind.Reacquisition, required.RequestIdentifier),
            _ => throw new AgentCredentialAccessException(),
        };
        await new AgentCredentialLeaseSet(result.Lease, null, null).UseAsync(async (material, _, token) =>
        {
            await this.accountUpsertService.UpsertForTokenAsync(material!, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        this.accountUpsertCompleted = true;
    }

    // Resumes the persisted Copilot session on the first creation when a resume id was provided, so
    // prior conversation history remains visible to the model (issue #3); otherwise creates a fresh
    // session. The resume id is one-shot. If resuming fails (for example the on-disk session has been
    // removed), fall back to creating a new session so the chat stays usable.
    private async Task<ICopilotSession> CreateOrResumeSessionAsync(ChatOptions? options, CancellationToken cancellationToken)
    {
        var resumeSessionId = this.pendingResumeSessionId;
        this.pendingResumeSessionId = null;

        if (!string.IsNullOrWhiteSpace(resumeSessionId))
        {
            try
            {
                var resumeConfig = BuildResumeSessionConfig(this.modelId, this.byokOptions, options, this.modelOptions, this.builtinToolPolicy);
                if (!string.IsNullOrWhiteSpace(this.workingDirectoryOverride))
                {
                    resumeConfig.WorkingDirectory = this.workingDirectoryOverride;
                }
                return await this.copilotClient!
                    .ResumeSessionAsync(resumeSessionId, resumeConfig, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not AgentCredentialAccessException)
            {
                this.loggerFactory?.CreateLogger<CopilotSdkChatClient>().LogWarning(
                    exception,
                    "Failed to resume Copilot session {SessionId}; creating a new session.",
                    resumeSessionId);
            }
        }

        var sessionConfig = BuildSessionConfig(this.modelId, this.byokOptions, options, this.modelOptions, this.builtinToolPolicy);
        if (!string.IsNullOrWhiteSpace(this.workingDirectoryOverride))
        {
            sessionConfig.WorkingDirectory = this.workingDirectoryOverride;
        }
        return await this.copilotClient!.CreateSessionAsync(sessionConfig, cancellationToken).ConfigureAwait(false);
    }

    // Issue #1443: resolves model.options.executor (via the shared executor-bindings path) and, when
    // it maps to a non-local connection-descriptor, connects a transport and builds a session-only
    // remote ICopilotClient. Returns null — so the caller uses the in-process CLI factory — when no
    // bindings/registry are wired or when the descriptor is local. A named but missing binding
    // fails closed before any local or remote client is opened.
    private async Task<ICopilotClient?> TryCreateRemoteClientAsync(CancellationToken cancellationToken)
    {
        if (this.executorBindings is null)
        {
            if (this.requiresRemoteProvider)
            {
                throw new InvalidOperationException("Remote model execution requires its configured executor binding.");
            }
            if (!string.IsNullOrWhiteSpace(this.modelExecutorName))
            {
                throw new InvalidOperationException(
                    $"Executor binding '{this.modelExecutorName}' could not be resolved.");
            }

            return null;
        }

        var descriptor = this.executorBindings.ResolveComponent(this.modelExecutorName);

        if (IsLocalDescriptor(descriptor))
        {
            if (this.requiresRemoteProvider)
            {
                throw new InvalidOperationException("Remote model execution cannot fall back to a local executor.");
            }
            return null;
        }
        if (this.executorTransportFactoryRegistry is null)
        {
            throw new InvalidOperationException(
                "Remote Copilot execution requires a configured executor transport registry.");
        }
        if ((this.byokOptions is not null || !string.IsNullOrWhiteSpace(this.remoteProviderReference))
            && !CopilotSessionTransportFrames.IsValidProviderReference(this.remoteProviderReference))
        {
            throw new InvalidOperationException(
                "Remote BYOK execution requires a valid worker-local remoteProvider reference.");
        }
        var reference = this.executionTrustContext?.RemoteReference;
        if (this.executionTrustContext is not null && reference is null)
        {
            throw new InvalidOperationException(
                "Remote Copilot execution requires a named trust-profile reference; inline trust profiles cannot be transported.");
        }

        var transport = await this.executorTransportFactoryRegistry
            .ConnectToAsync(descriptor, cancellationToken)
            .ConfigureAwait(false);
        return new CopilotClientOverTransport(
            transport,
            reference,
            this.remoteSessionTimeProvider,
            this.remoteSessionStartupTimeout,
            this.remoteSessionTerminalTimeout,
            this.remoteProviderReference,
            this.loggerFactory);
    }

    internal static bool IsLocalDescriptor(JsonElement descriptor)
    {
        if (descriptor.ValueKind != JsonValueKind.Object
            || !descriptor.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(type.GetString()))
        {
            throw new InvalidOperationException(
                "The Copilot executor binding is malformed.");
        }

        return string.Equals(
            type.GetString(),
            ExecutionTargetResolver.LocalDescriptorType,
            StringComparison.OrdinalIgnoreCase);
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
    // SessionConfig.WorkingDirectory / ResumeSessionConfig.WorkingDirectory (session level). When
    // no override is present, the value defaults to the user's home directory rather than null so
    // that the CLI session always starts in a valid directory (issue #1462).
    private static string? GetWorkingDirectory(ChatOptions? options)
    {
        if (options?.AdditionalProperties?.TryGetValue("working-directory", out var chatValue) == true
            && chatValue is string chatDir
            && !string.IsNullOrEmpty(chatDir))
        {
            return chatDir;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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

internal sealed record SteeringDeliveryOutcome(ChatMessage Message, Exception? Failure)
{
    internal bool Delivered => this.Failure is null;
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
/// <param name="RetainAcceptedSendFailure">
/// Seals an unsuccessful native start when it already owns output. Returns true only when the
/// reader must expose that output and its failure instead of disposing it or retrying the send.
/// </param>
internal sealed record StreamingTurnContext(
    System.Threading.Channels.ChannelReader<ChatResponseUpdate> Reader,
    IAsyncDisposable Subscription,
    Func<CancellationToken, Task> SendAsync,
    Func<Task> OnCancelledAsync,
    Func<Task> OnPipeBrokenAsync,
    Func<Exception, bool>? RetainAcceptedSendFailure = null);

internal sealed class CredentialBoundCopilotClient(
    ICopilotClient inner,
    AgentCredentialLeaseSet credentials,
    CopilotByokOptions? byokOptions) : ICopilotClient
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        credentials.GuardAsync(inner.StartAsync, cancellationToken);

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken) =>
        credentials.GuardAsync(inner.ListModelsAsync, cancellationToken);

    public Task<ICopilotSession> CreateSessionAsync(
        SessionConfig config,
        CancellationToken cancellationToken) =>
        credentials.UseAsync(async (material, modelOptions, token) =>
        {
            if (byokOptions is not null)
            {
                config.Provider = CopilotSdkChatClient.CreateProviderConfig(byokOptions, config.Model!, modelOptions);
                config.Provider.ApiKey = material;
            }
            var session = await inner.CreateSessionAsync(config, token).ConfigureAwait(false);
            return (ICopilotSession)new CredentialBoundCopilotSession(session, credentials);
        }, cancellationToken);

    public Task<ICopilotSession> ResumeSessionAsync(
        string sessionId,
        ResumeSessionConfig config,
        CancellationToken cancellationToken) =>
        credentials.UseAsync(async (material, modelOptions, token) =>
        {
            if (byokOptions is not null)
            {
                config.Provider = CopilotSdkChatClient.CreateProviderConfig(byokOptions, config.Model!, modelOptions);
                config.Provider.ApiKey = material;
            }
            var session = await inner.ResumeSessionAsync(sessionId, config, token).ConfigureAwait(false);
            return (ICopilotSession)new CredentialBoundCopilotSession(session, credentials);
        }, cancellationToken);

    public Task<EventsReadResult> ReadPersistedEventsAsync(
        ReadCopilotPersistedEventsRequest request,
        CancellationToken cancellationToken) =>
        credentials.GuardAsync(token => inner.ReadPersistedEventsAsync(request, token), cancellationToken);

    public Task<IReadOnlyList<AgentRegistryLiveTargetEntry>> ListLiveSessionStatesAsync(CancellationToken cancellationToken) =>
        credentials.GuardAsync(inner.ListLiveSessionStatesAsync, cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

internal sealed class CredentialBoundCopilotSession(
    ICopilotSession inner,
    AgentCredentialLeaseSet credentials) : ICopilotSession
{
    public string SessionId => inner.SessionId;
    public IDisposable Subscribe(Action<SessionEvent> handler) => inner.Subscribe(handler);

    public Task<AssistantMessageEvent?> SendAndWaitAsync(
        MessageOptions options,
        TimeSpan? timeout,
        CancellationToken cancellationToken) =>
        credentials.GuardAsync(token => inner.SendAndWaitAsync(options, timeout, token), cancellationToken);

    public Task SendAsync(
        MessageOptions options,
        CancellationToken cancellationToken) =>
        credentials.GuardAsync(token => inner.SendAsync(options, token), cancellationToken);

    public Task AbortAsync(CancellationToken cancellationToken) =>
        inner.AbortAsync(cancellationToken);

    public Task<IReadOnlyList<TaskInfo>> ListTasksAsync(CancellationToken cancellationToken) =>
        credentials.GuardAsync(inner.ListTasksAsync, cancellationToken);

    public Task SetModelAsync(
        string modelId,
        CancellationToken cancellationToken) =>
        credentials.GuardAsync(token => inner.SetModelAsync(modelId, token), cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
