using AgentSchema;
using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using MongoDB.Bson;
using OllamaSharp;
using OpenAI;
using Phantom.Workspaces.Llm.Echo;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.SlashCommands;
using System.ClientModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Core session model for an agent conversation.
/// Owns the <see cref="AgentInputQueueManager"/> and drives the processing loop.
/// Exposes observable collections and events that consumers (e.g. ViewModels, CLI) can
/// subscribe to and marshal onto their own thread as needed.
/// Events and collection mutations run on the processing loop scheduler: this is
/// the current synchronization context if one is present when the chat is created,
/// otherwise a dedicated exclusive scheduler that serializes foreground work so the
/// running-item collections are never mutated concurrently off the UI thread.
/// </summary>
public sealed class AgentChat : IAsyncDisposable, IServiceProvider, ISubAgentChatRegistry, IRunningSubAgent, ISubAgentTable
{
    private const string GitHubModelsInferenceEndpoint = "https://models.github.ai/inference";
    private const string RunningPartAssistantReasoning = "assistant-reasoning";
    private const string RunningPartAssistantText = "assistant-text";

    private readonly object sessionLock = new();
    private readonly InternalCreateAgentChatRequest request;
    private readonly TimeProvider timeProvider;
    private AgentChatSession? session;
    private AgentDefinition? agentDefinition;
    private IChatClient? client;
    private AgentFrameworkChatHistoryProvider? chatHistoryProvider;
    private IncrementalPersistenceChatHistoryProvider? persistenceProvider;
    private ChatClientAgent? chatClientAgent;
    private ChatClientAgentOptions? chatOptions;

#pragma warning disable MAAI001
    /// <summary>
    /// Test hook: exposes the resolved <c>UseProvidedChatClientAsIs</c> flag from the
    /// underlying <see cref="ChatClientAgentOptions"/> so tests can verify that hosted
    /// sub-agent pipelines do not have <c>FunctionInvokingChatClient</c> wrapped around
    /// them (bug #1174).
    /// </summary>
    internal bool? UseProvidedChatClientAsIs => this.chatOptions?.UseProvidedChatClientAsIs;
#pragma warning restore MAAI001

    private IReadOnlyList<RuntimeContextProviderRegistration> runtimeContextProviderRegistrations = [];
    private readonly AgentInputQueueManager queueManager;
    private readonly AgentChatQueueManager chatQueueManager;
    private AgentChatHistoryService? historyService;
    private readonly AgentChatHistoryCollection history = new();
    private readonly TaskCompletionSource historyPopulated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AgentChatRunningItemCollection runningItems = new();
    private readonly AgentRunningItems runningItemOperations;
    private readonly ObservableCollection<AgentChatPendingApprovalItem> pendingApprovalItems = [];
    private readonly List<IAsyncDisposable> ownedResources;
    private readonly object ownedResourcesLock = new();
    private readonly object toolsLock = new();
    private readonly Dictionary<string, ToolStateNode> toolIndex = new(StringComparer.Ordinal);
    private readonly List<ToolStateNode> toolRoots = [];
    private readonly SemaphoreSlim toolMutationLock = new(1, 1);
    private readonly CancellationTokenSource cts = new();
    private readonly SlashCommandRegistry outerSlashCommands = new();
    private readonly ReplaceableSlashCommandHandlerRegistry replaceableCommands = new();
    private Task processTask = Task.CompletedTask;
    private string agentSessionId = Guid.NewGuid().ToString("n");

    private bool isBusy;
    private bool processingStarted;
    private readonly object processingStateLock = new();
    private CancellationTokenSource? activeRunCancellation;
    private int disposeStarted;

    // Sub-agent registry
    private readonly Dictionary<string, (AgentChat Chat, SubAgentChatClient Client)> subAgentMap =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> parentToolCallIdToAgentId = new(StringComparer.Ordinal);
    private readonly object subAgentsLock = new();
    private readonly ObservableCollection<IRunningSubAgent> subAgentItems = [];
    private readonly Dictionary<string, SubAgent> subAgentTableMap = new(StringComparer.Ordinal);
    private SubAgentChatClient? subAgentChatClientSource;
    private string agentId = string.Empty;
    private AgentChat? parentAgent;
    private bool acceptsUserInput = true;
    private DateTime lastUpdatedAt;
    private AgentChatCompletionState? completionStateOverride;

    // Steering messages injected mid-run (via ToolResultSteeringMiddleware or CopilotSdkChatClient)
    // are injected into the active PartialResponseConflator so they appear at the tool-result
    // boundary where they were sent to the agent, not before or after the full turn.
    // When no run is active, steering adds directly to History.
    // Guarded by steeringLock because the Copilot path fires SteeringMessageForwarded from
    // the UI thread while the processing loop runs on its own task.
    private PartialResponseConflator? activeConflator;
    private readonly object steeringLock = new();

    // Serializes foreground work when the chat is created without a synchronization context (for
    // example in tests or non-GUI hosts). In production the captured UI synchronization context
    // already runs foreground work one-at-a-time; this provides the same single-threaded guarantee
    // off the UI thread so the running-item collections are never mutated concurrently.
    private readonly ConcurrentExclusiveSchedulerPair foregroundSchedulerPair = new();

    // Captured at construction (on the creating thread, e.g. the UI thread) so the processing loop
    // runs on the foreground synchronization context. Capturing later (once the loop starts) is
    // unreliable because framework awaits on the initialization path drop the context. When no
    // synchronization context is present, the exclusive scheduler above serializes foreground work
    // so the process loop and the partial-response conflator never mutate the running-item
    // collections from two threads at once.
    private readonly TaskScheduler foregroundScheduler;

    // Test-only accessor for verifying foreground-scheduler flow through sub-agent creation
    // paths (issue #913).
    internal TaskScheduler ForegroundSchedulerForTesting => this.foregroundScheduler;

    internal AgentChat(InternalCreateAgentChatRequest request)
    {
       VerifyOnForegroundContext(request.ForegroundScheduler);
       this.request = request;
       this.timeProvider = request.TimeProvider;
       this.lastUpdatedAt = this.timeProvider.GetUtcNow().UtcDateTime;
       this.queueManager = new AgentInputQueueManager();
       this.chatQueueManager = new AgentChatQueueManager(this.queueManager);
       this.runningItemOperations = new AgentRunningItems(this.runningItems);
       this.ownedResources = request.OwnedResources?.ToList() ?? [];
       this.PendingApprovalItems = new ReadOnlyObservableCollection<AgentChatPendingApprovalItem>(this.pendingApprovalItems);
       this.SubAgents = new ReadOnlyObservableCollection<IRunningSubAgent>(this.subAgentItems);
       this.foregroundScheduler = request.ForegroundScheduler
           ?? (SynchronizationContext.Current is not null
               ? TaskScheduler.FromCurrentSynchronizationContext()
               : this.foregroundSchedulerPair.ExclusiveScheduler);
       this.outerSlashCommands.Register(this.replaceableCommands);
       this.outerSlashCommands.Register(new HelpSlashCommandHandler(this.outerSlashCommands));
    }

    // Enforces the foreground-context affinity invariant (issue #909): AgentChat construction and
    // initialization must happen on the foreground context, never on a background thread that
    // merely holds a reference to the foreground scheduler. The invariant is only verifiable for
    // SynchronizationContextTaskScheduler, which exposes its context; plain schedulers (e.g.
    // TaskScheduler.Default in headless hosts such as the CLI and tests) carry no thread affinity
    // to verify. When no scheduler is provided, the foreground context is captured from the
    // current thread and is therefore consistent by construction. Verification fails fast rather
    // than marshalling, so caller bugs surface immediately instead of silently binding downstream
    // UI machinery to the wrong thread (issue #908).
    private static void VerifyOnForegroundContext(TaskScheduler? foregroundScheduler)
    {
        if (foregroundScheduler is SynchronizationContextTaskScheduler contextScheduler
            && !contextScheduler.IsOnSynchronizationContext
            && TaskScheduler.Current != contextScheduler)
        {
            throw new InvalidOperationException(
                "AgentChat must be constructed and initialized on its foreground context (the UI thread in GUI hosts). "
                + "The provided ForegroundScheduler's SynchronizationContext is not current on this thread. "
                + "Invoke creation on the foreground context instead of a background thread (issue #909).");
        }
    }

    internal static Task<AgentChat> CreateAsync(InternalCreateAgentChatRequest request)
        => CreateAsync(request, onConstructed: null);

    // The onConstructed hook runs on the construction (foreground) context after the chat is
    // constructed but before InitializeAsync, so callers (tests) can observe running-item
    // mutations that occur during initialization (issue #1068).
    internal static async Task<AgentChat> CreateAsync(
        InternalCreateAgentChatRequest request,
        Action<AgentChat>? onConstructed)
    {
       var chat = new AgentChat(request);
       onConstructed?.Invoke(chat);
       await chat.InitializeAsync();
       return chat;
    }

    private async Task InitializeAsync()
    {
       PersistedAgent? restoredAgent = null;
       if (!string.IsNullOrWhiteSpace(this.request.AgentSessionId))
       {
           restoredAgent = await this.request.ConfiguredStore.RestoreAsync(
               new RestoreRequest
               {
                   AgentSessionId = this.request.AgentSessionId,
               },
               this.request.CancellationToken);
       }

       var restoredAgentDefinitionJson = restoredAgent.HasValue ? restoredAgent.Value.AgentDefinitionJson : null;
       var restoredAgentSessionJson = restoredAgent.HasValue ? restoredAgent.Value.AgentSessionJson : null;

       // #1140: Seed the in-memory last-activity timestamp from the persisted UpdatedUtc so
       // restored (already-completed) sub-agent cards show the true "N days ago" time rather
       // than the reload time. Without this, construction stamps lastUpdatedAt = now (see
       // ctor) and RestoreSubAgentsAsync's #1128 forced SetCompletionState would then re-stamp
       // it. Stores that don't track a timestamp return null and we keep the construction-time
       // value.
       if (restoredAgent is { LastUpdatedUtc: { } persistedUpdatedUtc })
       {
           this.lastUpdatedAt = persistedUpdatedUtc;
       }

       var resolvedAgentDefinition = this.request.AgentDefinition
           ?? (restoredAgentDefinitionJson is not null
               ? AgentDefinition.FromJson(restoredAgentDefinitionJson.ToJson())
               : null);
       if (resolvedAgentDefinition is null && restoredAgent.HasValue)
       {
           // Fix #1187: legacy hosted Copilot sub-agents rehydrate with a null
           // AgentDefinitionJson (the empty-definition case behind #1186). Substitute the
           // canonical full hosted-Copilot sub-agent definition so downstream code (and
           // AgentFactory model resolution in particular) always sees a well-formed
           // document rather than propagating the null through to the model-null throw.
           resolvedAgentDefinition = CopilotSubAgentDefinitionDefaults.Create(
               subAgentSessionId: restoredAgent.Value.AgentSessionId,
               displayName: this.request.DisplayNameOverride,
               description: this.request.DescriptionOverride,
               name: this.request.NameOverride);
       }
       if (resolvedAgentDefinition is null)
       {
           throw new InvalidOperationException("Agent definition could not be resolved from request or persistence store.");
       }

       this.agentDefinition = resolvedAgentDefinition;
       var innerRegistry = new SlashCommandRegistry();
       var servicesWithRegistry = this.request.AgentServices is not null
           ? this.request.AgentServices with { SlashCommandRegistry = innerRegistry }
           : new AgentServices { SlashCommandRegistry = innerRegistry };
       var clientInfo = this.request.ClientOverride is not null
           ? new ChatClientResult(this.request.ClientOverride, this.request.DisplayNameOverride ?? string.Empty)
           : this.request.ChatClientFactoryOverride is not null
               ? new ChatClientResult(
                   await this.request.ChatClientFactoryOverride(this.request.CancellationToken).ConfigureAwait(false),
                   this.request.DisplayNameOverride ?? string.Empty)
               : await AgentFactory.CreateChatClientAsync(
                   resolvedAgentDefinition,
                   servicesWithRegistry,
                   queueManager: this.queueManager,
                   subAgentChatRegistry: this,
                   cancellationToken: this.request.CancellationToken).ConfigureAwait(false);
       this.replaceableCommands.Current = innerRegistry;
       var resolvedClient = clientInfo.ChatClient;
       this.acceptsUserInput = resolvedClient is not IHostedAgentChatClient;
       if (resolvedClient is SubAgentChatClient sac)
       {
           this.subAgentChatClientSource = sac;

           // SubAgentChatClient.Complete/Fail run on the Copilot SDK event drain loop (a
           // thread-pool thread). Re-raising synchronously would run UI subscribers
           // (RunningSubAgentDisplay → WebView bridge) off the UI thread, which now fails loudly
           // (issue #913) — marshal onto the chat's foreground scheduler like every other
           // foreground mutation.
           sac.CompletionStateChanged += (_, _) => _ = Task.Factory.StartNew(
               () => this.CompletionStateChanged?.Invoke(this, EventArgs.Empty),
               CancellationToken.None,
               TaskCreationOptions.DenyChildAttach,
               this.foregroundScheduler);
       }

       var useProvidedChatClientAsIs= this.request.OverrideUseProvidedChatClientAsIs
           ?? ResolveUseProvidedChatClientAsIs(
               this.request.ClientOverride is not null,
               resolvedClient);
       if (this.request.AgentServices?.LogChat == true)
       {
           resolvedClient = resolvedClient.AsBuilder().UseLogging(this.request.AgentServices.LoggerFactory).Build();
       }

       this.client = resolvedClient;
       this.DisplayName = this.request.DisplayNameOverride ?? clientInfo.DisplayName;
       this.Description = this.request.DescriptionOverride ?? resolvedAgentDefinition.Description ?? string.Empty;
       // #1151: caller-supplied sub-agent name/id (e.g. fix-crash1142) is distinct from the
       // type-level DisplayName. Blank when no caller name was supplied so downstream UI can
       // fall back to DisplayName / session id without inventing a fake value.
       this.Name = this.request.NameOverride ?? string.Empty;

       // Steering messages are injected into the model call deep in the chat-client pipeline
       // (at tool-result boundaries by ToolResultSteeringMiddleware, or forwarded to the live
       // Copilot session by CopilotSdkChatClient). Subscribe so those injected messages are also
       // recorded in the visible chat history (issue #17).
       if (resolvedClient.GetService(typeof(ToolResultSteeringMiddleware)) is ToolResultSteeringMiddleware steeringMiddleware)
       {
           steeringMiddleware.MessagesInjected += injected => this.AppendSteeringMessagesToHistory(injected);
       }

       if (resolvedClient.GetService(typeof(CopilotSdkChatClient)) is CopilotSdkChatClient copilotChatClient)
       {
           copilotChatClient.SteeringMessageForwarded += message => this.AppendSteeringMessagesToHistory([message]);
           // Fix #1109: RunningAgentChatFactory is mandatory for any AgentChat that hosts a Copilot
           // SDK client — the sub-agent router has no fallback path if it's missing. Fail fast so
           // callers cannot construct an AgentChat that would silently misroute sub-agent output
           // (issue #1110) into the parent transcript.
           var runningChatFactory = this.request.AgentServices?.RunningAgentChatFactory as IRunningAgentChatFactory
               ?? throw new InvalidOperationException(
                   "AgentServices.RunningAgentChatFactory must be supplied at construction time. " +
                   "AgentChatFactory injects itself via WithSelfAsFactory; ensure this AgentChat was " +
                   "created through IAgentChatFactory or the request explicitly carries a factory.");
           copilotChatClient.SetSubAgentDependencies(runningChatFactory, this);
       }

       if (resolvedClient is IAsyncDisposable asyncDisposableClient)
       {
           this.RegisterOwnedResource(asyncDisposableClient);
       }

       this.persistenceProvider = new IncrementalPersistenceChatHistoryProvider(resolvedAgentDefinition, this.request.ConfiguredStore);
       this.chatHistoryProvider = new AgentFrameworkChatHistoryProvider(this.persistenceProvider);
       var streamingMiddleware = new StreamingPersistenceMiddleware(resolvedClient, this.persistenceProvider, this.request.ConfiguredStore);
       this.chatHistoryProvider.InvocationStarting += (_, args) => streamingMiddleware.SetCurrentSession(args.Session);
       this.client = streamingMiddleware;
       this.historyService = new AgentChatHistoryService(this.History, this.chatHistoryProvider, this.timeProvider);
#pragma warning disable MAAI001
       this.chatOptions = new ChatClientAgentOptions
       {
           ChatOptions = new ChatOptions(),
           ChatHistoryProvider = this.chatHistoryProvider,
           UseProvidedChatClientAsIs = useProvidedChatClientAsIs,
           RequirePerServiceCallChatHistoryPersistence = !useProvidedChatClientAsIs,
       };
#pragma warning restore MAAI001
       AgentFactory.ConfigureChatOptions(resolvedAgentDefinition, this.chatOptions.ChatOptions);
       this.runtimeContextProviderRegistrations = await this.CreateRuntimeContextProviderRegistrationsAsync(
           resolvedAgentDefinition,
           this.request.AgentServices,
           this.request.CancellationToken);
       this.chatOptions.AIContextProviders = this.runtimeContextProviderRegistrations
           .Where(registration => registration.Provider is not null)
           .Select(registration => new ToolFilteringAIContextProvider(
               registration.Provider!,
               this.IsToolEnabledForRuntime))
           .ToArray();

       this.chatClientAgent = new ChatClientAgent(streamingMiddleware, this.chatOptions);
       this.persistenceProvider.SetSessionSerializer(
           async (session, token) =>
           {
               var serializedSession = await this.chatClientAgent.SerializeSessionAsync(
                   session,
                   cancellationToken: token);
               return serializedSession.ToBsonDocument();
           });

       var frameworkSession = restoredAgentSessionJson is not null
           ? await this.chatClientAgent.DeserializeSessionAsync(
               restoredAgentSessionJson.ToJsonElement()
                   ?? throw new InvalidOperationException("Stored agent session JSON could not be read."),
               cancellationToken: this.request.CancellationToken)
           : await this.chatClientAgent.CreateSessionAsync(this.request.CancellationToken);

       if (!string.IsNullOrWhiteSpace(this.request.AgentSessionId))
       {
           this.persistenceProvider.SetAgentSessionId(frameworkSession, this.request.AgentSessionId);
       }

       // Resume the GitHub Copilot CLI session (and its model-visible history) after a restart by
       // replaying the stored SDK session id, and keep the persisted id current as new sessions are
       // established (issue #3). Uses the ICopilotSdkSessionSink capability so remote-hosted
       // SDK sessions (behind ChatClientOverTransport) participate too (issue #1319).
       if (resolvedClient.GetService(typeof(Phantom.Workspaces.Transport.Chat.ICopilotSdkSessionSink))
               is Phantom.Workspaces.Transport.Chat.ICopilotSdkSessionSink copilotSdkClient)
       {
           var restoredCopilotSdkSessionId = restoredAgent?.CopilotSdkSessionId;
           if (!string.IsNullOrWhiteSpace(restoredCopilotSdkSessionId))
           {
               copilotSdkClient.SetResumeSessionId(restoredCopilotSdkSessionId);
               this.persistenceProvider.SetCopilotSdkSessionId(restoredCopilotSdkSessionId);
           }

           copilotSdkClient.SessionEstablished += establishedSessionId =>
               this.persistenceProvider.SetCopilotSdkSessionId(establishedSessionId);
       }

       var resolvedAgentSessionId = this.persistenceProvider.ExtractAgentSessionId(frameworkSession);
       var persistedMessages = await this.request.ConfiguredStore.ReadMessagesAsync(
           new ReadMessagesRequest { AgentSessionId = resolvedAgentSessionId },
           this.request.CancellationToken);

       // RunSessionInitAsync runs the running-item mutations, the initial persisted-history load
       // (History.Add fires CollectionChanged), and historyPopulated.TrySetResult(). These mutate
       // the non-thread-safe, UI-observed collections and so must run on the foreground scheduler.
       //
       // The continuation after the .ConfigureAwait(false) on the CreateChatClientAsync await (see
       // the chat-client resolution above) is NOT guaranteed to run on the foreground context: when
       // that await genuinely suspends (the real Copilot SDK client), the captured
       // SynchronizationContext/scheduler is discarded and the rest of this method resumes on a
       // thread-pool thread even when foregroundScheduler is a SynchronizationContextTaskScheduler
       // over the UI thread (issues #1084 / #1068 / #1072). So RunSessionInitAsync is always
       // dispatched onto the foreground scheduler via RunOnForegroundAsync and awaited (mirroring
       // the tool-init dispatch and EnqueueSystemNote/EnqueueHelpNote).
       //
       // Awaiting the dispatched foreground work here can never deadlock because production
       // foreground schedulers are self-draining: the UI SynchronizationContextTaskScheduler pumps
       // via the dispatcher, and the headless ConcurrentExclusiveSchedulerPair.ExclusiveScheduler
       // drains via the thread pool -- so the dispatched task runs while this method is suspended
       // awaiting it (issues #1098 / #1084). The session-init running item is fully completed before
       // the processing loop starts, so it never races the loop.
       async Task RunSessionInitAsync()
       {
           var sessionInitItem = this.CreateRunningItem(new AgentChatHistoryItem
           {
               Role = AgentChatHistoryItem.DiagnosticChatRole,
               Contents = new AIContent[] { new TextContent("Loading session") },
               Timestamp = this.timeProvider.GetUtcNow(),
           });
           try
           {
               this.LoadInitialHistory(persistedMessages);

               // Signal that persisted history has been loaded into History. Consumers (e.g. the
               // chat output control) await this before taking the initial history snapshot so the
               // first render never captures an empty/partial History (issue #1009).
               this.historyPopulated.TrySetResult();

               this.SetSession(new AgentChatSession(this.chatClientAgent, frameworkSession));
               this.SetAgentSessionId(resolvedAgentSessionId);

               if (!string.IsNullOrWhiteSpace(this.request.AgentSessionId))
               {
                   await this.RestoreSubAgentsAsync(this.request.CancellationToken);
               }

               // The session-init step is transient progress only: clear the running item without
               // echoing "Loading session" into the History transcript. Failures (below) still
               // surface an error diagnostic (issue #1072).
               this.CompleteRunningItem(sessionInitItem, writeToHistory: false);
           }
           catch (Exception ex)
           {
               this.UpdateRunningItem(sessionInitItem, [new AgentChatHistoryItem
               {
                   Role = AgentChatHistoryItem.DiagnosticChatRole,
                   Contents = new AIContent[] { new ErrorContent($"Failed to load session: {ex}") },
                   Timestamp = this.timeProvider.GetUtcNow(),
               }]);
               this.CompleteRunningItem(sessionInitItem, writeToHistory: true);
               throw;
           }
       }

       await this.RunOnForegroundAsync(RunSessionInitAsync);

       this.StartProcessingLoop();

       // Tool initialization mutates running items (one per toolset / MCP server) and must be
       // serialized with the processing loop on the foreground scheduler (issue #1068). Only
       // dispatch when there is actual tool work: a tool-less agent performs no running-item
       // mutations, so skipping the dispatch keeps CreateAsync from blocking on a foreground
       // scheduler that defers execution until externally pumped (e.g. sub-agent restore tests).
       var hasToolWork = this.runtimeContextProviderRegistrations.Count > 0;
       if (hasToolWork)
       {
           await this.RunOnForegroundAsync(
               () => this.InitializeMcpToolsAsync(this.request.CancellationToken));
       }
    }

    // Binds the continuation chain of the supplied action to the foreground scheduler, mirroring
    // StartProcessingLoop's Task.Factory.StartNew(..., foregroundScheduler) pattern so that
    // running-item mutations performed by the action are serialized with the processing loop and
    // never mutate the non-thread-safe running-item collections concurrently (issue #1068).
    private Task RunOnForegroundAsync(Func<Task> action) =>
        Task.Factory.StartNew(
            action,
            this.cts.Token,
            TaskCreationOptions.DenyChildAttach,
            this.foregroundScheduler).Unwrap();

    /// <summary>
    /// Fired when the active streaming turn finishes.
    /// The argument is the completed <see cref="AgentChatHistoryItem"/> that was added to <see cref="History"/>.
    /// Fires on the background processing thread.
    /// </summary>
    public event EventHandler<AgentChatHistoryItem>? TurnCompleted;

    public event EventHandler<string>? AgentSessionIdChanged;

    public event EventHandler? ToolsChanged;

    public event EventHandler? UsageChanged;

    /// <summary>
    /// Fired when the completion state of this agent changes.
    /// Only relevant for sub-agents; root agents always remain in <see cref="AgentChatCompletionState.Running"/> state.
    /// </summary>
    public event EventHandler? CompletionStateChanged;

    /// <summary>Completed conversation turns, in order.</summary>
    public AgentChatHistoryCollection History => this.history;

    /// <summary>
    /// Completes once persisted history has been loaded into <see cref="History"/> during
    /// initialization. Await this before snapshotting <see cref="History"/> to avoid rendering an
    /// empty/partial history on first open (issue #1009).
    /// </summary>
    public Task HistoryPopulated => this.historyPopulated.Task;

    /// <summary>Currently executing agent response items.</summary>
    public AgentChatRunningItemCollection RunningItems => this.runningItems;

    /// <summary>All known input queues, including the default queue.</summary>
    public ReadOnlyObservableCollection<AgentChatQueue> InputQueues => this.chatQueueManager.InputQueues;

    /// <summary>The default input queue.</summary>
    public AgentChatQueue DefaultInputQueue => this.chatQueueManager.DefaultInputQueue;

    /// <summary>System queue that bypasses queued scheduling.</summary>
    public AgentChatQueue ImmediateInputQueue => this.chatQueueManager.ImmediateInputQueue;

    /// <summary>Items awaiting user approval.</summary>
    public ReadOnlyObservableCollection<AgentChatPendingApprovalItem> PendingApprovalItems { get; }

    /// <summary>Underlying queue manager, for advanced queue behaviors.</summary>
    public AgentInputQueueManager InputQueueManager => this.queueManager;

    public AgentChatQueueManager QueueManager => this.chatQueueManager;

    public bool IsBusy => this.isBusy;

    /// <summary>The agent ID used to identify this chat within its parent's sub-agent registry.</summary>
    /// <remarks>
    /// Fix #1152: Falls back to <see cref="AgentSessionId"/> when the tool-call-driven
    /// <c>agentId</c> field has never been assigned. This ensures every AgentChat exposes a
    /// stable, non-empty navigation key — needed for root chats (whose <c>agentId</c> is only
    /// set by AddSubAgent's tool-call path, which the root never enters) and for sub-agents
    /// registered through <see cref="ISubAgentTable.Add"/>. Without this, the UI emitted
    /// empty <c>data-navigate-agent-id</c> attributes and clicks collapsed into no-ops.
    /// </remarks>
    public string AgentId => string.IsNullOrEmpty(this.agentId) ? this.agentSessionId : this.agentId;

    /// <summary>The parent chat that spawned this sub-agent, or <see langword="null"/> for root agents.</summary>
    public AgentChat? ParentAgent => this.parentAgent;

    /// <summary>True when the underlying chat client accepts direct user input; false for hosted sub-agents.</summary>
    public bool AcceptsUserInput => this.acceptsUserInput;

    /// <summary>Completion state of this sub-agent chat. Always <see cref="AgentChatCompletionState.Running"/> for root agents.</summary>
    public AgentChatCompletionState CompletionState =>
        this.completionStateOverride
        ?? this.subAgentChatClientSource?.CompletionState
        ?? AgentChatCompletionState.Running;

    /// <summary>The last time this chat's state was updated.</summary>
    public DateTime LastUpdatedAt => this.lastUpdatedAt;

    /// <summary>Child sub-agent chats spawned by this chat during the current session.</summary>
    public ReadOnlyObservableCollection<IRunningSubAgent> SubAgents { get; }

    IReadOnlyList<IRunningSubAgent> IRunningSubAgent.SubAgents => this.SubAgents;

    public string DisplayName { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    /// <summary>
    /// Caller-supplied sub-agent name/id (e.g. <c>fix-crash1142</c>) from
    /// <c>SubagentStartedData.AgentName</c>. Empty for root chats and for sub-agents whose
    /// caller did not supply a name. Distinct from <see cref="DisplayName"/>, which carries the
    /// agent-type label.
    /// </summary>
    public string Name { get; private set; } = string.Empty;

    public string AgentSessionId => this.agentSessionId;

    public AgentDefinition? AgentDefinition => this.agentDefinition;

    /// <summary>
    /// The slash commands available for this chat session.
    /// Provider-specific commands (e.g. <c>/working-directory</c> for GitHub Copilot) are
    /// registered automatically during initialisation; <c>/help</c> is always present.
    /// </summary>
    public ISlashCommandRegistry SlashCommands => this.outerSlashCommands;

    public long? TotalInputTokenCount { get; private set; }

    public long? TotalOutputTokenCount { get; private set; }

    /// <summary>Session total of cache-read (prompt-cache hit) input tokens, when the provider reports them.</summary>
    public long? TotalCacheReadTokenCount { get; private set; }

    /// <summary>Session total of cache-write (prompt-cache fill) input tokens, when the provider reports them.</summary>
    public long? TotalCacheWriteTokenCount { get; private set; }

    /// <summary>Session total of reasoning tokens, when the provider reports them.</summary>
    public long? TotalReasoningTokenCount { get; private set; }

    /// <summary>Session total dollar cost in micro-USD, summed from provider-reported per-call cost.</summary>
    public long? TotalSessionCostMicroUsd { get; private set; }

    /// <summary>Session total dollar cost in USD, or <c>null</c> when no cost data is available.</summary>
    public double? TotalSessionCostUsd
        => this.TotalSessionCostMicroUsd is long micro ? micro / 1_000_000.0 : null;

    public IReadOnlyList<AgentChatToolItem> Tools => this.GetToolSnapshot();

    public IReadOnlyList<AgentChatToolItem> GetToolSnapshot()
    {
        lock (this.toolsLock)
        {
            return this.toolRoots.Select(CreateToolSnapshot).ToArray();
        }
    }

    internal void RaiseToolsChanged()
    {
        this.ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetToolEnabledAsync(
        string toolId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            throw new ArgumentException("Tool id is required.", nameof(toolId));
        }

        await this.toolMutationLock.WaitAsync(cancellationToken);
        try
        {
            var changed = false;
            lock (this.toolsLock)
            {
                if (!this.toolIndex.TryGetValue(toolId, out var node))
                {
                    return;
                }

                changed = SetNodeEnabled(node, enabled);
                if (node.Parent is not null)
                {
                    RecomputeAncestorEnabled(node.Parent);
                }
            }

            if (!changed)
            {
                return;
            }

            this.ToolsChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            this.toolMutationLock.Release();
        }
    }

    /// <summary>
    /// Updates the live <see cref="ChatOptions.AdditionalProperties"/> with the supplied parameter
    /// values so that the next turn picks up the change.  The key mapping is 1:1: for example the
    /// <c>working-directory</c> parameter value is written directly to
    /// <c>AdditionalProperties["working-directory"]</c>, which <see cref="CopilotSdkChatClient"/>
    /// reads when computing the session signature and building the session config.
    /// </summary>
    public void UpdateParameterValues(IReadOnlyDictionary<string, string> parameterValues)
    {
        ArgumentNullException.ThrowIfNull(parameterValues);
        var chatOptions = this.chatOptions?.ChatOptions;
        if (chatOptions is null)
        {
            return;
        }

        chatOptions.AdditionalProperties ??= [];
        foreach (var (key, value) in parameterValues)
        {
            chatOptions.AdditionalProperties[key] = value;
        }
    }

    /// <summary>
    /// Runs a single agent turn directly, bypassing the input queue, and streams back the
    /// resulting <see cref="ChatResponseUpdate"/>s. History prepended to the LLM call is
    /// sourced from the configured <see cref="Phantom.Workspaces.Llm.Interfaces.IAgentPersistenceStore"/>;
    /// with <see cref="NullAgentPersistenceStore"/> only the caller-supplied messages are forwarded.
    /// </summary>
    /// <remarks>
    /// This method is intended for headless (server-side) use by <see cref="AgentChatSessionCache"/>
    /// where UI state management (running items, history conflation) is not required. It must not be
    /// called concurrently with <see cref="EnqueueUserContents"/> on the same instance.
    /// </remarks>
    public async IAsyncEnumerable<ChatResponseUpdate> RunSingleTurnAsync(
        IReadOnlyList<ChatMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var session = this.GetSession();
        var runOptions = this.CreateRunOptions();
        await foreach (var update in session
            .RunStreamAsync(messages.ToArray(), runOptions, cancellationToken)
            .AsChatResponseUpdatesAsync()
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <summary>
    /// Adds a user message to the target queue and waits for submission before history is created.
    /// </summary>
    public void EnqueueUserMessage(string text)
    {
        this.EnqueueUserMessage(text, this.DefaultInputQueue);
    }

    /// <summary>
    /// Adds a text-only user message and enqueues it for processing.
    /// </summary>
    public void EnqueueUserMessage(string text, AgentChatQueue targetQueue)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        this.EnqueueUserContents([new TextContent(text)], targetQueue);
    }

    /// <summary>
    /// Adds a user message with structured content (e.g. text + images) and enqueues it.
    /// </summary>
    public void EnqueueUserContents(IReadOnlyList<AIContent> contents, AgentChatQueue? targetQueue = null)
    {
        ArgumentNullException.ThrowIfNull(contents);
        if (contents.Count == 0)
        {
            return;
        }

        targetQueue ??= this.DefaultInputQueue;
        this.StartProcessingLoop();
        this.queueManager.Enqueue(
            targetQueue.Queue,
            [
                new AgentInputItem
                {
                    Messages =
                    [
                        new ChatMessage(ChatRole.User, contents.ToList())
                        {
                            CreatedAt = this.timeProvider.GetUtcNow(),
                        },
                    ],
                },
            ]);
    }

    /// <summary>
    /// Adds a diagnostic note to the visible chat history without forwarding it to the LLM.
    /// Used for slash-command status messages and other host-side notifications.
    /// </summary>
    public void EnqueueSystemNote(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _ = Task.Factory.StartNew(
            () =>
            {
                this.AddHistoryItem(new AgentChatHistoryItem
                {
                    Role = AgentChatHistoryItem.DiagnosticChatRole,
                    Contents = [new TextContent(text)],
                    Timestamp = this.timeProvider.GetUtcNow(),
                });
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            this.foregroundScheduler);
    }

    public void EnqueueHelpNote(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _ = Task.Factory.StartNew(
            () =>
            {
                this.AddHistoryItem(new AgentChatHistoryItem
                {
                    Role = AgentChatHistoryItem.HelpChatRole,
                    Contents = [new TextContent(text)],
                    Timestamp = this.timeProvider.GetUtcNow(),
                });
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            this.foregroundScheduler);
    }

    /// <summary>
    /// Adds a transient slash-command result to the visible chat history as a non-persisted
    /// diagnostic note. The note is added to in-memory <see cref="History"/> only via
    /// <see cref="AddHistoryItem"/>; it is never written to <c>ConfiguredStore</c>, so it does
    /// not reappear after a reload (issue #1396).
    /// </summary>
    public void EnqueueTransientDiagnostic(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _ = Task.Factory.StartNew(
            () =>
            {
                this.AddHistoryItem(new AgentChatHistoryItem
                {
                    Role = AgentChatHistoryItem.DiagnosticChatRole,
                    Contents = [new TextContent(text)],
                    Timestamp = this.timeProvider.GetUtcNow(),
                });
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            this.foregroundScheduler);
    }

    /// <summary>
    /// Requests an interrupt of the current streaming response.
    /// </summary>
    public void Interrupt()
    {
        CancellationTokenSource? cancellationToUse;
        lock (this.processingStateLock)
        {
            cancellationToUse = this.activeRunCancellation;
        }

        cancellationToUse?.Cancel();
    }

    public void ResetSession(AgentChatSession nextSession, bool interruptCurrentResponse = true)
    {
        ArgumentNullException.ThrowIfNull(nextSession);

        if (interruptCurrentResponse)
        {
            this.Interrupt();
        }

        this.queueManager.Enqueue(
            this.queueManager.ImmediateQueue,
            [
                new AgentInputItem
                {
                    Messages = [],
                    ResetSession = nextSession,
                },
            ]);
    }

    public AgentChatRunningItem CreateRunningItem(params AgentChatHistoryItem[] items)
    {
        return this.runningItemOperations.Create(items);
    }

    public void UpdateRunningItem(AgentChatRunningItem item, AgentChatHistoryItem[] model)
    {
        this.runningItemOperations.Update(item, model);
    }

    public void CompleteRunningItem(
        AgentChatRunningItem item,
        bool writeToHistory = true)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (writeToHistory)
        {
            // Snapshot before iterating: the foreground scheduler may concurrently modify
            // item.Items via SyncItems, so enumeration without a snapshot can throw
            // "Collection was modified; enumeration operation may not execute."
            foreach (var historyItem in item.Items.ToArray())
            {
                this.AddHistoryItem(historyItem);
                this.TurnCompleted?.Invoke(this, historyItem);
            }
        }

        this.runningItemOperations.Remove(item);
    }

    /// <summary>
    /// Moves the supplied stable items from the running item into <see cref="History"/>.
    /// Only items whose running item is still active are promoted; promotions that arrive after
    /// <see cref="CompleteRunningItem"/> has removed the running item are silently dropped to
    /// prevent double-adds in exception paths where the processing loop completes the item before
    /// all pending conflator dispatches have executed.
    /// </summary>
    public void PromoteItemsToHistory(AgentChatRunningItem runningItem, AgentChatHistoryItem[] items)
    {
        ArgumentNullException.ThrowIfNull(runningItem);
        ArgumentNullException.ThrowIfNull(items);

        if (!this.runningItems.Contains(runningItem))
        {
            return;
        }

        foreach (var historyItem in items)
        {
            this.AddHistoryItem(historyItem);
            this.TurnCompleted?.Invoke(this, historyItem);
        }
    }

    private void AddHistoryItem(AgentChatHistoryItem item)
    {
        this.lastUpdatedAt = this.timeProvider.GetUtcNow().UtcDateTime;
        this.History.Add(item);
    }

    private void AppendUserMessagesToHistory(IReadOnlyList<ChatMessage> requestMessages)
    {
        foreach (var message in requestMessages)
        {
            if (message.Role != ChatRole.User)
            {
                continue;
            }

            var nextItem = new AgentChatHistoryItem
            {
                Role = ChatRole.User,
                Contents = message.Contents.ToArray(),
                Timestamp = this.timeProvider.GetUtcNow(),
            };

            this.History.Add(nextItem);
        }
    }

    // Steering messages injected mid-run must not be added to History immediately: the streaming
    // assistant response is still in RunningItems, so adding the steering message now would place
    // it before the assistant turn in the History list. Buffer the items and flush them to History
    // in RunProcessLoopAsync after CompleteRunningItem moves the assistant turn to History.
    private void AppendSteeringMessagesToHistory(IReadOnlyList<ChatMessage> requestMessages)
    {
        var items = new List<AgentChatHistoryItem>();
        foreach (var message in requestMessages)
        {
            if (message.Role != ChatRole.User)
            {
                continue;
            }

            items.Add(new AgentChatHistoryItem
            {
                Role = ChatRole.User,
                Contents = message.Contents.ToArray(),
                Timestamp = this.timeProvider.GetUtcNow(),
            });
        }

        if (items.Count == 0)
        {
            return;
        }

        PartialResponseConflator? conflator;
        lock (this.steeringLock)
        {
            conflator = this.activeConflator;
        }

        if (conflator is not null)
        {
            // Active run: inject into the conflator at the current update-count boundary so the
            // steering message appears at the tool-result boundary where it was sent to the agent.
            foreach (var item in items)
            {
                conflator.InjectInterstitialAfterCurrentUpdates(item);
            }
        }
        else
        {
            // No active run — add directly to history.
            foreach (var item in items)
            {
                this.AddHistoryItem(item);
            }
        }
    }

    public void RegisterOwnedResource(IAsyncDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (this.ownedResourcesLock)
        {
            this.ownedResources.Add(resource);
        }
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(ISubAgentTable))
            return this;
        return this.chatClientAgent?.GetService(serviceType)
            ?? this.request.AgentServices?.GetService(serviceType);
    }

    /// <inheritdoc/>
    public ISubAgentChat? TryGet(string agentId)
    {
        lock (this.subAgentsLock)
        {
            return this.subAgentMap.TryGetValue(agentId, out var entry) ? entry.Client : null;
        }
    }

    /// <summary>
    /// Returns the child <see cref="AgentChat.AgentId"/> that was spawned by the tool call with the
    /// given <paramref name="parentToolCallId"/>, or <see langword="null"/> if no such mapping exists.
    /// </summary>
    public string? TryGetSubAgentIdByToolCallId(string parentToolCallId)
    {
        lock (this.subAgentsLock)
        {
            return this.parentToolCallIdToAgentId.TryGetValue(parentToolCallId, out var agentId) ? agentId : null;
        }
    }

    /// <summary>
    /// Synchronously resolves a registered sub-agent by its session id from the authoritative
    /// <see cref="subAgentTableMap"/>. The map is populated under <see cref="subAgentsLock"/> the
    /// instant a sub-agent is registered (via <see cref="ISubAgentTable.Add"/> or lazy restore),
    /// whereas the <see cref="SubAgents"/> observable collection is filled asynchronously on the
    /// foreground scheduler and can lag under load. Resolving through the map closes a race
    /// (issue #1386) where a just-registered or stop-with-disposed session was momentarily absent
    /// from the observable collection and therefore reported "not found".
    /// </summary>
    internal SubAgent? TryGetRegisteredSubAgent(string sessionId)
    {
        lock (this.subAgentsLock)
        {
            return this.subAgentTableMap.TryGetValue(sessionId, out var subAgent) ? subAgent : null;
        }
    }

    /// <inheritdoc/>
    public async Task<ISubAgentChat> GetOrCreateAsync(
        string agentId,
        AgentDefinition subAgentDefinition,
        string parentToolCallId,
        CancellationToken cancellationToken = default)
    {
        lock (this.subAgentsLock)
        {
            if (this.subAgentMap.TryGetValue(agentId, out var existing))
            {
                return existing.Client;
            }
        }

        var chatClient = new SubAgentChatClient(agentId, subAgentDefinition.Name ?? agentId, subAgentDefinition.Description ?? string.Empty, this.timeProvider);

        // Fix for issue #913: without ForegroundScheduler the child chat falls back to its own
        // ConcurrentExclusiveSchedulerPair, so every "foreground" mutation (UpdateRunningItem,
        // PromoteItemsToHistory) runs on thread-pool threads and downstream UI machinery (e.g.
        // the WebView auto-flush DispatcherTimer) binds to a dispatcher that never pumps,
        // silently dropping all live sub-agent output. Flow the parent's foreground scheduler to
        // the child and construct it on that scheduler — mirroring
        // AgentChatFactory.CreateChatOnForegroundAsync — which also satisfies the #909
        // construction-affinity guard.
        var childChat = await Task.Factory.StartNew(
            () => AgentChat.CreateAsync(new InternalCreateAgentChatRequest
            {
                AgentDefinition = subAgentDefinition,
                ConfiguredStore = this.request.ConfiguredStore,
                ClientOverride = chatClient,
                DisplayNameOverride = subAgentDefinition.Name ?? agentId,
                DescriptionOverride = subAgentDefinition.Description ?? string.Empty,
                ForegroundScheduler = this.foregroundScheduler,
                CancellationToken = cancellationToken,
                TimeProvider = this.timeProvider,
            }),
            cancellationToken,
            TaskCreationOptions.DenyChildAttach,
            this.foregroundScheduler).Unwrap();
        childChat.agentId = agentId;
        childChat.parentAgent = this;

        AgentChat? toDispose = null;
        ISubAgentChat result;
        lock (this.subAgentsLock)
        {
            if (this.subAgentMap.TryGetValue(agentId, out var racing))
            {
                toDispose = childChat;
                result = racing.Client;
            }
            else
            {
                this.subAgentMap[agentId] = (childChat, chatClient);
                this.parentToolCallIdToAgentId[parentToolCallId] = agentId;
                result = chatClient;
            }
        }

        if (toDispose is not null)
        {
            _ = toDispose.DisposeAsync();
            return result;
        }

        await Task.Factory.StartNew(
            () => this.subAgentItems.Add(childChat),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            this.foregroundScheduler);

        var parentSessionId = this.agentSessionId;
        var childSessionId = childChat.AgentSessionId;

        await this.request.ConfiguredStore.StoreAsync(
            new StoreRequestAgent
            {
                Agent = new PersistedAgent
                {
                    AgentSessionId = childSessionId,
                    AgentDefinitionJson = MongoDB.Bson.BsonDocument.Parse(subAgentDefinition.ToJson()),
                },
            },
            cancellationToken);
        await this.request.ConfiguredStore.AddSubAgentLinkAsync(parentSessionId, childSessionId, cancellationToken);

        return result;
    }

    /// <inheritdoc/>
    async Task<SubAgent> ISubAgentTable.Add(AgentChat agentChat)
    {
        // Fix #1152: When a sub-agent is registered via ISubAgentTable.Add (the manual path,
        // used by hosted sub-agents and tests) rather than through AddSubAgent's tool-call flow,
        // its ``agentId`` field is never seeded and AgentId returns "". The UI's
        // NavigateToSubAgent then can't route to it (and RunningSubAgents HTML emits an empty
        // ``data-navigate-agent-id`` attribute). Fall back to AgentSessionId so every registered
        // sub-agent has a stable, non-empty navigation id.
        if (string.IsNullOrEmpty(agentChat.agentId))
        {
            agentChat.agentId = agentChat.AgentSessionId;
        }

        var sessionId = new AgentSessionId(agentChat.AgentSessionId);
        var factory = this.request.AgentServices?.RunningAgentChatFactory as IRunningAgentChatFactory;
        var subAgent = new SubAgent(sessionId, agentChat, factory);

        lock (this.subAgentsLock)
        {
            if (this.subAgentTableMap.ContainsKey(agentChat.AgentSessionId))
            {
                throw new InvalidOperationException(
                    $"A sub-agent with session ID '{agentChat.AgentSessionId}' is already registered.");
            }

            this.subAgentTableMap[agentChat.AgentSessionId] = subAgent;
        }

        _ = Task.Factory.StartNew(
            () => this.subAgentItems.Add(subAgent),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            this.foregroundScheduler);

        await this.request.ConfiguredStore.AddSubAgentLinkAsync(this.agentSessionId, agentChat.AgentSessionId);

        return subAgent;
    }

    public void SetAgentSessionId(string agentSessionId)
    {
        if (string.IsNullOrWhiteSpace(agentSessionId))
        {
            throw new ArgumentException("Agent session id is required.", nameof(agentSessionId));
        }

        if (string.Equals(this.agentSessionId, agentSessionId, StringComparison.Ordinal))
        {
            return;
        }

        this.agentSessionId = agentSessionId;

        this.AgentSessionIdChanged?.Invoke(this, agentSessionId);
    }

    internal void SetCompletionState(AgentChatCompletionState state)
        => this.SetCompletionState(state, preserveLastUpdatedAt: false);

    /// <summary>
    /// Sets the sub-agent completion-state override.
    /// </summary>
    /// <param name="state">The new completion state.</param>
    /// <param name="preserveLastUpdatedAt">
    /// When <see langword="true"/>, the transition does NOT bump
    /// <see cref="LastUpdatedAt"/>. Used by the restore path so that forcing a persisted
    /// (already-completed) sub-agent to its terminal state on reload does not overwrite the
    /// seeded, persisted last-activity timestamp with the reload time (issue #1140). The event
    /// still fires so UI subscribers (running-item markers) clear as required by #1128.
    /// </param>
    internal void SetCompletionState(
        AgentChatCompletionState state,
        bool preserveLastUpdatedAt)
    {
        // #1128: Idempotent — bail if the override is already at the requested state so
        // repeated calls (e.g. from restore + a stray terminal event) don't re-fire the
        // completion notification and cause duplicate UI updates.
        if (this.completionStateOverride == state)
        {
            return;
        }

        this.completionStateOverride = state;
        if (!preserveLastUpdatedAt)
        {
            // #1140: Only stamp lastUpdatedAt for genuine activity-driven state changes.
            this.lastUpdatedAt = this.timeProvider.GetUtcNow().UtcDateTime;
        }

        // #1128: SetCompletionState is the only path that flips a sub-agent to a terminal
        // state on reload (RestoreSubAgentsAsync) and — until now — did not raise the event
        // the UI listens on, so restored sub-agents' pulsating-brain / running markers never
        // cleared. Marshal onto the injected foreground scheduler (#909/#913/#1122
        // invariant) so UI subscribers observe the change on the UI thread.
        _ = Task.Factory.StartNew(
            () => this.CompletionStateChanged?.Invoke(this, EventArgs.Empty),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            this.foregroundScheduler);
    }

    private async Task RestoreSubAgentsAsync(CancellationToken cancellationToken)
    {
        var childIds = await this.request.ConfiguredStore.ReadSubAgentChildIdsAsync(
            this.agentSessionId, cancellationToken);

        if (childIds.Count == 0)
        {
            return;
        }

        // Fix #1109: RunningAgentChatFactory is mandatory. The previous null-tolerant warn-and-skip
        // branch let restored sub-agents silently vanish and then leak their output into the parent
        // transcript (issue #1110). Fail fast with a clear message.
        var factory = this.request.AgentServices?.RunningAgentChatFactory as IRunningAgentChatFactory
            ?? throw new InvalidOperationException(
                $"Cannot restore {childIds.Count} sub-agent(s): AgentServices.RunningAgentChatFactory " +
                "is required but was not supplied. AgentChatFactory.WithSelfAsFactory injects itself " +
                "for chats created through the factory; construct this chat through IAgentChatFactory " +
                "or supply an explicit RunningAgentChatFactory in AgentServices.");

        foreach (var childId in childIds)
        {
            var stub = new SubAgent(childId, factory);
            lock (this.subAgentsLock)
            {
                this.subAgentTableMap[childId.Value] = stub;
            }
            _ = Task.Factory.StartNew(
                () => this.subAgentItems.Add(stub),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                this.foregroundScheduler);

            // #1128: A reloaded sub-agent's SDK run is no longer executing, so no terminal
            // Complete/Fail event will ever arrive to move it out of the default Running
            // state; the UI would otherwise show a perpetual pulsating brain / running
            // marker. Materialise the child eagerly, hold the lease for the lifetime of the
            // parent chat (so subsequent lease acquisitions — e.g. AgentViewModel's
            // AddSubAgentSlotLazy — see the same AgentChat with the terminal override
            // already applied), and force it Succeeded via SetCompletionState (which now
            // raises CompletionStateChanged on the child's foreground scheduler).
            var terminalTask = this.MarkRestoredSubAgentTerminalAsync(stub, cancellationToken);
            lock (this.restoredSubAgentTerminalTasksLock)
            {
                this.restoredSubAgentTerminalTasks.Add(terminalTask);
            }
        }
    }

    private readonly List<Task> restoredSubAgentTerminalTasks = new();
    private readonly object restoredSubAgentTerminalTasksLock = new();

    /// <summary>
    /// Test-only: awaits completion of every fire-and-forget "mark restored sub-agent
    /// terminal" task queued by <see cref="RestoreSubAgentsAsync"/>. Enables deterministic
    /// verification of the #1128 restore transition without polling.
    /// </summary>
    internal Task WaitForRestoredSubAgentsMarkedTerminalAsync()
    {
        Task[] tasks;
        lock (this.restoredSubAgentTerminalTasksLock)
        {
            tasks = this.restoredSubAgentTerminalTasks.ToArray();
        }
        return Task.WhenAll(tasks);
    }

    private Task MarkRestoredSubAgentTerminalAsync(
        SubAgent stub,
        CancellationToken cancellationToken)
    {
        // #1186: Previously this method acquired a full lease on the child stub
        // (SubAgent.AcquireLeaseAsync -> AgentChatFactory.GetAsync -> full
        // AgentChat.CreateAsync -> AgentChat.InitializeAsync -> AgentFactory.CreateChatClientAsync)
        // just to flip the terminal completion state. For hosted Copilot sub-agents whose
        // persisted AgentDefinition was empty (Model == null), CreateChatClientAsync's
        // null-model guard threw "Agent definition does not specify a model.", faulting
        // the whole restore path and hanging the startup splash indefinitely.
        //
        // Restored sub-agents are receive-only stubs whose SDK run is long gone, so we do
        // not need a real IChatClient or a validated model to represent their terminal
        // state. Record the override on the stub itself; SubAgent.AcquireLeaseAsync applies
        // it lazily when (and only when) a caller — e.g. AgentViewModel's
        // AddSubAgentSlotLazy — actually needs the child materialised. In the meantime,
        // IRunningSubAgent.CompletionState surfaces the restored state directly, so the
        // pulsating-brain / running marker never appears for a reloaded terminal child.
        _ = cancellationToken; // no I/O; nothing to cancel
        stub.SetRestoredCompletionState(AgentChatCompletionState.Succeeded);
        return Task.CompletedTask;
    }

    private void LoadInitialHistory(IReadOnlyList<ChatMessage>? initialMessages)
    {
        if (initialMessages is null || initialMessages.Count == 0)
        {
            return;
        }

        foreach (var message in initialMessages)
        {
            this.AddHistoryItem(new AgentChatHistoryItem
            {
                Role = message.Role,
                Contents = message.Contents.ToArray(),
                Timestamp = message.CreatedAt,
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposeStarted, 1) != 0)
        {
            return;
        }

        await this.cts.CancelAsync();
        try
        {
            await this.processTask;
        }
        catch (OperationCanceledException)
        {
        }

        this.cts.Dispose();
        List<IAsyncDisposable> resourcesToDispose;
        lock (this.ownedResourcesLock)
        {
            resourcesToDispose = [.. this.ownedResources];
            this.ownedResources.Clear();
        }

        foreach (var resource in resourcesToDispose)
        {
            await resource.DisposeAsync();
        }

        List<AgentChat> childChats;
        lock (this.subAgentsLock)
        {
            childChats = [.. this.subAgentMap.Values.Select(static v => v.Chat)];
            this.subAgentMap.Clear();
        }

        foreach (var childChat in childChats)
        {
            await childChat.DisposeAsync();
        }
    }

    // Drains a conflator while suppressing coalesce faults so a secondary failure during teardown
    // cannot mask the cancellation or provider error already being handled.
    private static async Task DrainQuietlyAsync(PartialResponseConflator conflator)
    {
        try
        {
            await conflator.DrainAsync();
        }
        catch
        {
        }
    }

    // Conflates streaming partial-response updates. Every update is appended to the accumulating
    // list so the accumulator is always complete, but at most one coalesce runs at a time. When a
    // coalesce finishes, the next one reads the latest accumulated state, so intermediate frames are
    // skipped while the final frame is always processed. Coalescing the accumulated updates into chat
    // messages is inherently O(n) per update (O(n^2) over a run), so the list is built on a background
    // task; the running-item population then runs as a separate task on the captured foreground
    // scheduler (the UI thread in production), so the UI-bound collections are only mutated there.
    // To minimise downstream collection churn, CoalesceAsync re-uses the cached AgentChatHistoryItem
    // reference from the previous frame whenever an item's content is structurally unchanged.  This
    // lets SyncItems' reference-equality guard suppress Replace notifications (and the HTML re-render
    // work they would trigger) for every item that did not actually change in this streaming tick.
    private sealed class PartialResponseConflator
    {
        private readonly AgentChat owner;
        private readonly AgentChatRunningItem runningItem;
        private readonly TaskScheduler foregroundScheduler;
        private readonly List<AgentResponseUpdate> updates = new();
        private readonly List<(int AfterUpdateCount, AgentChatHistoryItem Item)> interstitials = new();
        private readonly object gate = new();
        private long version;
        private long processedVersion;
        private bool workerRunning;
        private Task worker = Task.CompletedTask;
        private AgentChatHistoryItem[] cachedItems = [];
        private int promotedCount;

        public PartialResponseConflator(AgentChat owner, AgentChatRunningItem runningItem)
        {
            this.owner = owner;
            this.runningItem = runningItem;
            this.foregroundScheduler = owner.foregroundScheduler;
        }

        public void Notify(AgentResponseUpdate update)
        {
            bool startWorker = false;
            lock (this.gate)
            {
                this.updates.Add(update);
                this.version++;
                if (!this.workerRunning)
                {
                    this.workerRunning = true;
                    startWorker = true;
                }
            }

            if (startWorker)
            {
                this.worker = this.RunWorkerAsync();
            }
        }

        /// <summary>
        /// Records a steering message to appear at the current update-count boundary in the
        /// running item. Steering injected at update N will appear after the items coalesced
        /// from updates 0..N-1 and before those from N onwards — i.e. at the tool-result boundary
        /// where it was sent to the agent.
        /// </summary>
        internal void InjectInterstitialAfterCurrentUpdates(AgentChatHistoryItem item)
        {
            bool startWorker = false;
            lock (this.gate)
            {
                this.interstitials.Add((this.updates.Count, item));
                this.version++;
                if (!this.workerRunning)
                {
                    this.workerRunning = true;
                    startWorker = true;
                }
            }

            if (startWorker)
            {
                this.worker = this.RunWorkerAsync();
            }
        }

        // Awaited after the producer loop ends. Processes any final stable items that were not yet
        // promoted during the streaming worker loop, then clears the running item so CompleteRunningItem
        // finds an empty Items collection and avoids re-adding already-promoted items to History.
        public async Task DrainAsync()
        {
            Task workerTask;
            lock (this.gate)
            {
                workerTask = this.worker;
            }

            await workerTask;

            // After the worker finishes all queued versions, the last active item (if any) is now
            // stable. Promote remaining items and clear the running item.
            var finalItems = this.cachedItems;
            if (this.promotedCount < finalItems.Length)
            {
                var toPromote = finalItems[this.promotedCount..];
                await Task.Factory.StartNew(
                    () => this.owner.PromoteItemsToHistory(this.runningItem, toPromote),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    this.foregroundScheduler);
                this.promotedCount = finalItems.Length;
            }

            await Task.Factory.StartNew(
                () => this.owner.UpdateRunningItem(this.runningItem, []),
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                this.foregroundScheduler);
        }

        private async Task RunWorkerAsync()
        {
            while (true)
            {
                AgentResponseUpdate[] snapshot;
                (int AfterUpdateCount, AgentChatHistoryItem Item)[] interstitialSnapshot;
                long targetVersion;
                lock (this.gate)
                {
                    if (this.processedVersion == this.version)
                    {
                        this.workerRunning = false;
                        return;
                    }

                    targetVersion = this.version;
                    snapshot = this.updates.ToArray();
                    interstitialSnapshot = [..this.interstitials];
                }

                // Capture the current cached items before the background task so that the
                // background work does not race with a foreground assignment to cachedItems.
                var previousItems = this.cachedItems;

                // Build the chat history items on a background task (does not touch the foreground).
                var chatHistoryItems = await Task.Run(() => CoalesceAsync(snapshot, previousItems, interstitialSnapshot, this.owner.timeProvider));

                // Store the newly-coalesced result so the next cycle can reuse stable references.
                this.cachedItems = chatHistoryItems;

                // Items 0..stableCount-1 are stable (the last item is still receiving tokens).
                // Promote any not yet promoted to History.
                var stableCount = chatHistoryItems.Length > 1 ? chatHistoryItems.Length - 1 : 0;
                if (stableCount > this.promotedCount)
                {
                    var toPromote = chatHistoryItems[this.promotedCount..stableCount];
                    await Task.Factory.StartNew(
                        () => this.owner.PromoteItemsToHistory(this.runningItem, toPromote),
                        CancellationToken.None,
                        TaskCreationOptions.DenyChildAttach,
                        this.foregroundScheduler);
                    this.promotedCount = stableCount;
                }

                // Populate the running item with only the active tail on the foreground scheduler
                // so the UI-bound collection is only ever mutated there.
                await Task.Factory.StartNew(
                    () => this.owner.UpdateRunningItem(this.runningItem, chatHistoryItems[this.promotedCount..]),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    this.foregroundScheduler);

                lock (this.gate)
                {
                    this.processedVersion = targetVersion;
                }
            }
        }

        private static async Task<AgentChatHistoryItem[]> CoalesceAsync(
            AgentResponseUpdate[] snapshot,
            AgentChatHistoryItem[] previous,
            (int AfterUpdateCount, AgentChatHistoryItem Item)[] interstitials,
            TimeProvider timeProvider)
        {
            AgentChatHistoryItem[] newItems;

            if (interstitials.Length == 0)
            {
                // Fast path: no interstitials, single pass.
                newItems = await CoalesceSegmentAsync(snapshot, timeProvider);
            }
            else
            {
                // Interstitials mark "inject this item after the first N streaming updates."
                // Split the snapshot at each boundary, coalesce each segment independently,
                // then interleave the injected items so they appear at the right position.
                var result = new List<AgentChatHistoryItem>();
                int segStart = 0;

                foreach (var (afterCount, item) in interstitials.OrderBy(static x => x.AfterUpdateCount))
                {
                    int segEnd = Math.Min(afterCount, snapshot.Length);
                    if (segEnd > segStart)
                    {
                        result.AddRange(await CoalesceSegmentAsync(snapshot[segStart..segEnd], timeProvider));
                    }

                    result.Add(item);
                    segStart = segEnd;
                }

                // Tail: remaining updates after the last interstitial.
                if (segStart < snapshot.Length)
                {
                    result.AddRange(await CoalesceSegmentAsync(snapshot[segStart..], timeProvider));
                }

                newItems = result.ToArray();
            }

            // Add a blank assistant placeholder if the full snapshot ends with a tool result,
            // indicating the agent is still waiting to respond to the tool output.
            var lastIsToolResult = snapshot.Length > 0
                && snapshot[^1].Contents.OfType<ToolResultContent>().Any();
            if (lastIsToolResult)
            {
                newItems = [..newItems, new AgentChatHistoryItem { Role = ChatRole.Assistant, Timestamp = timeProvider.GetUtcNow() }];
            }

            // Re-use the cached reference for each item whose content is structurally unchanged.
            // This lets AgentRunningItems.SyncItems' ReferenceEquals guard suppress unnecessary
            // Replace notifications (and their downstream HTML re-render work) for stable items.
            ReuseUnchangedItemReferences(previous, newItems);
            return newItems;
        }

        // Converts a slice of streaming updates into AgentChatHistoryItems. An empty slice
        // returns an empty array; no assistant placeholder is added here (that is handled by the
        // caller on the full snapshot).
        private static Task<AgentChatHistoryItem[]> CoalesceSegmentAsync(AgentResponseUpdate[] updates, TimeProvider timeProvider)
            => Task.FromResult(AgentResponseUpdateCoalescer.Coalesce(updates, timeProvider));

        /// <summary>
        /// For each position where the new item is structurally identical to the cached previous
        /// item, replaces the new instance with the cached one so callers can use reference
        /// equality to detect unchanged items cheaply.
        /// </summary>
        private static void ReuseUnchangedItemReferences(
            AgentChatHistoryItem[] previous,
            AgentChatHistoryItem[] current)
        {
            var reuseCount = Math.Min(previous.Length, current.Length);
            for (var i = 0; i < reuseCount; i++)
            {
                if (AreStructurallyEqual(previous[i], current[i]))
                {
                    current[i] = previous[i];
                }
            }
        }

        internal static bool AreStructurallyEqual(AgentChatHistoryItem a, AgentChatHistoryItem b)
        {
            if (!string.Equals(a.Role.Value, b.Role.Value, StringComparison.Ordinal))
            {
                return false;
            }

            var ac = a.Contents;
            var bc = b.Contents;
            if (ac.Count != bc.Count)
            {
                return false;
            }

            for (var i = 0; i < ac.Count; i++)
            {
                if (!AreContentsEqual(ac[i], bc[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreContentsEqual(AIContent a, AIContent b)
        {
            if (a.GetType() != b.GetType())
            {
                return false;
            }

            return (a, b) switch
            {
                (TextContent ta, TextContent tb) => ta.Text == tb.Text,
                (TextReasoningContent ra, TextReasoningContent rb) => ra.Text == rb.Text,
                (FunctionCallContent ca, FunctionCallContent cb) =>
                    ca.CallId == cb.CallId && ca.Name == cb.Name,
                (FunctionResultContent ra, FunctionResultContent rb) => ra.CallId == rb.CallId,
                (ErrorContent ea, ErrorContent eb) => ea.Message == eb.Message,
                _ => false,
            };
        }
    }

    private async Task RunProcessLoopAsync(
        CancellationToken cancellationToken)
    {
        var currentSession = this.GetSession();
        using var queueStateSignal = new SemaphoreSlim(0);
        void OnQueueStateChanged(object? sender, AgentInputQueueManager.QueueStateChangedEventArgs e)
            => queueStateSignal.Release();
        this.queueManager.QueueStateChanged += OnQueueStateChanged;

        List<ChatMessage> chatMessagesToSubmit = new List<ChatMessage>();

        try
        {
            this.isBusy = true;
            while (!cancellationToken.IsCancellationRequested)
            {
                chatMessagesToSubmit.Clear();
                while (chatMessagesToSubmit.Count == 0)
                {
                    while(this.queueManager.TryDequeueNextImmediateOrQueued(
                        out var agentInputItem))
                    {
                        if (agentInputItem.ResetSession != null)
                        {
                            this.SetSession(agentInputItem.ResetSession);
                            currentSession = this.GetSession();
                        }
                        chatMessagesToSubmit.AddRange(agentInputItem.Messages ?? Array.Empty<ChatMessage>());
                    }

                    if (chatMessagesToSubmit.Count == 0)
                    {
                        await queueStateSignal.WaitAsync(cancellationToken);
                    }
                }

                this.AppendUserMessagesToHistory(chatMessagesToSubmit);

                AgentChatRunningItem? currentPartialTextResponseItem = this.CreateRunningItem([
                    new AgentChatHistoryItem
                    {
                        Role = ChatRole.Assistant,
                        Timestamp = this.timeProvider.GetUtcNow(),
                    }]);

                // A fresh per-run cancellation source (linked to the loop token) is what Interrupt()
                // cancels, so a Ctrl+Break interrupts only the current run while the agent keeps
                // accepting new input afterwards.
                var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lock (this.processingStateLock)
                {
                    this.activeRunCancellation = runCancellation;
                }

                IAsyncEnumerator<AgentResponseUpdate>? providerEnumerator = null;
                Task<bool>? pendingMoveNext = null;
                PartialResponseConflator? partialResponses = null;
                try
                {
                    partialResponses = new PartialResponseConflator(
                        this,
                        currentPartialTextResponseItem
                            ?? throw new InvalidOperationException("Running item was unexpectedly null while starting a run."));

                    lock (this.steeringLock)
                    {
                        this.activeConflator = partialResponses;
                    }

                    providerEnumerator = this.StartRun(
                            chatMessagesToSubmit.ToArray(),
                            currentSession,
                            runCancellation.Token)
                        .GetAsyncEnumerator(runCancellation.Token);

                    while (true)
                    {
                        // Race each provider read against the interrupt token so a Ctrl+Break stops the
                        // loop promptly even if the provider/framework is slow to observe cancellation
                        // (for example while flushing end-of-run persistence).
                        pendingMoveNext = providerEnumerator.MoveNextAsync().AsTask();
                        if (await WasCanceledBeforeCompletingAsync(pendingMoveNext, runCancellation.Token))
                        {
                            throw new OperationCanceledException(runCancellation.Token);
                        }

                        var hasNext = await pendingMoveNext;
                        pendingMoveNext = null;
                        if (!hasNext)
                        {
                            break;
                        }

                        this.AccumulateUsage(providerEnumerator.Current);
                        partialResponses.Notify(providerEnumerator.Current);
                    }

                    // Flush the final accumulated frame (intermediate frames may have been skipped).
                    await partialResponses.DrainAsync();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The user interrupted the active run (Ctrl+Break). Record an "interrupted"
                    // diagnostic message after whatever partial content had already streamed in.
                    var runningItem = currentPartialTextResponseItem
                        ?? throw new InvalidOperationException("Running item was unexpectedly null while handling an interruption.");

                    // Drain first so no in-flight coalesce applies a frame after the diagnostic below.
                    if (partialResponses is not null)
                    {
                        await DrainQuietlyAsync(partialResponses);
                    }

                    var interruptedItems = runningItem.Items
                        // Snapshot before iterating: the foreground scheduler may still have an
                        // in-flight SyncItems call on runningItem.Items that would cause
                        // "Collection was modified" if we enumerate without a snapshot.
                        .ToArray()
                        .Concat([
                            new AgentChatHistoryItem
                            {
                                Role = AgentChatHistoryItem.DiagnosticChatRole,
                                Contents = [new TextContent("Interrupted by user.")],
                                Timestamp = this.timeProvider.GetUtcNow(),
                            },
                        ])
                        .ToArray();

                    this.UpdateRunningItem(runningItem, interruptedItems);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    var runningItem = currentPartialTextResponseItem
                        ?? throw new InvalidOperationException("Running item was unexpectedly null while handling a provider error.");
                    var errorItems = runningItem.Items
                        // Snapshot before iterating: same concurrent-modification risk as in
                        // the interrupt path above.
                        .ToArray()
                        .Concat([
                            new AgentChatHistoryItem
                            {
                                Role = AgentChatHistoryItem.DiagnosticChatRole,
                                Contents = [new ErrorContent($"Provider error: {ex}")],
                                Timestamp = this.timeProvider.GetUtcNow(),
                            },
                        ])
                        .ToArray();

                    this.UpdateRunningItem(runningItem, errorItems);
                }
                finally
                {
                    lock (this.processingStateLock)
                    {
                        if (ReferenceEquals(this.activeRunCancellation, runCancellation))
                        {
                            this.activeRunCancellation = null;
                        }
                    }

                    // Clean up the provider enumerator and run CTS in the background so a provider stuck
                    // on a canceled read cannot block the agent. The in-flight read is observed before
                    // disposing to honor the async-enumerator contract.
                    _ = CleanUpRunAsync(providerEnumerator, pendingMoveNext, runCancellation);

                    lock (this.steeringLock)
                    {
                        this.activeConflator = null;
                    }

                    this.CompleteRunningItem(currentPartialTextResponseItem);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            this.queueManager.QueueStateChanged -= OnQueueStateChanged;
            lock (this.processingStateLock)
            {
                this.isBusy = false;
                this.processingStarted = false;
                this.activeRunCancellation = null;
            }
        }
    }

    private async Task RunHostedProcessLoopAsync(
        CancellationToken cancellationToken)
    {
        var currentSession = this.GetSession();
        AgentChatRunningItem? runningItem = null;
        PartialResponseConflator? partialResponses = null;
        try
        {
            runningItem = this.CreateRunningItem([
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                    Timestamp = this.timeProvider.GetUtcNow(),
                }]);

            partialResponses = new PartialResponseConflator(
                this,
                runningItem);

            lock (this.steeringLock)
            {
                this.activeConflator = partialResponses;
            }

            var providerEnumerator = this.StartRun(
                    [],
                    currentSession,
                    cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            try
            {
                while (await providerEnumerator.MoveNextAsync())
                {
                    this.AccumulateUsage(providerEnumerator.Current);
                    partialResponses.Notify(providerEnumerator.Current);
                }
            }
            finally
            {
                await DisposeProviderEnumeratorAsync(providerEnumerator);
            }

            await partialResponses.DrainAsync();
            this.SetCompletionState(AgentChatCompletionState.Succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (runningItem is not null)
            {
                var errorItems = runningItem.Items
                    .ToArray()
                    .Concat([
                        new AgentChatHistoryItem
                        {
                            Role = AgentChatHistoryItem.DiagnosticChatRole,
                            Contents = [new ErrorContent($"Hosted sub-agent error: {ex.Message}")],
                            Timestamp = this.timeProvider.GetUtcNow(),
                        },
                    ])
                    .ToArray();

                this.UpdateRunningItem(runningItem, errorItems);
            }

            this.SetCompletionState(AgentChatCompletionState.Failed);
        }
        finally
        {
            if (partialResponses is not null)
            {
                await DrainQuietlyAsync(partialResponses);
            }

            lock (this.steeringLock)
            {
                if (ReferenceEquals(this.activeConflator, partialResponses))
                {
                    this.activeConflator = null;
                }
            }

            if (runningItem is not null)
            {
                this.CompleteRunningItem(runningItem);
            }

            lock (this.processingStateLock)
            {
                this.isBusy = false;
                this.processingStarted = false;
            }
        }
    }

    /// <summary>
    /// Cleans up a run's provider enumerator and cancellation source in the background. The in-flight
    /// read (if any) is awaited first so the enumerator is never disposed while a <c>MoveNextAsync</c>
    /// is still running; doing this in the background means a provider stuck on a canceled read cannot
    /// block the agent loop or an interrupt.
    /// </summary>
    private static async Task CleanUpRunAsync(
        IAsyncEnumerator<AgentResponseUpdate>? providerEnumerator,
        Task<bool>? pendingMoveNext,
        CancellationTokenSource runCancellation)
    {
        if (pendingMoveNext is not null)
        {
            try
            {
                await pendingMoveNext.ConfigureAwait(false);
            }
            catch
            {
                // The abandoned read was canceled or failed; nothing to surface from cleanup.
            }
        }

        if (providerEnumerator is not null)
        {
            await DisposeProviderEnumeratorAsync(providerEnumerator).ConfigureAwait(false);
        }

        runCancellation.Dispose();
    }

    private static async Task DisposeProviderEnumeratorAsync(
        IAsyncEnumerator<AgentResponseUpdate> providerEnumerator)
    {
        try
        {
            await providerEnumerator.DisposeAsync();
        }
        catch (NotSupportedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void AccumulateUsage(AgentResponseUpdate update)
    {
        var inputTokenCountToAdd = 0L;
        var outputTokenCountToAdd = 0L;
        var cacheReadTokenCountToAdd = 0L;
        var cacheWriteTokenCountToAdd = 0L;
        var reasoningTokenCountToAdd = 0L;
        var costMicroUsdToAdd = 0L;
        var hasInputTokenCount = false;
        var hasOutputTokenCount = false;
        var hasCacheReadTokenCount = false;
        var hasCacheWriteTokenCount = false;
        var hasReasoningTokenCount = false;
        var hasCost = false;

        foreach (var usageContent in update.Contents.OfType<UsageContent>())
        {
            if (usageContent.Details.InputTokenCount is long inputTokenCount)
            {
                inputTokenCountToAdd += inputTokenCount;
                hasInputTokenCount = true;
            }

            if (usageContent.Details.OutputTokenCount is long outputTokenCount)
            {
                outputTokenCountToAdd += outputTokenCount;
                hasOutputTokenCount = true;
            }

            var additionalCounts = usageContent.Details.AdditionalCounts;
            if (additionalCounts is not null)
            {
                if (additionalCounts.TryGetValue(CopilotSdkStreamAdapter.CacheReadTokensCountName, out var cacheRead))
                {
                    cacheReadTokenCountToAdd += cacheRead;
                    hasCacheReadTokenCount = true;
                }

                if (additionalCounts.TryGetValue(CopilotSdkStreamAdapter.CacheWriteTokensCountName, out var cacheWrite))
                {
                    cacheWriteTokenCountToAdd += cacheWrite;
                    hasCacheWriteTokenCount = true;
                }

                if (additionalCounts.TryGetValue(CopilotSdkStreamAdapter.ReasoningTokensCountName, out var reasoning))
                {
                    reasoningTokenCountToAdd += reasoning;
                    hasReasoningTokenCount = true;
                }

                if (additionalCounts.TryGetValue(CopilotSdkStreamAdapter.CostMicroUsdCountName, out var costMicroUsd))
                {
                    costMicroUsdToAdd += costMicroUsd;
                    hasCost = true;
                }
            }
        }

        var previousInputTokenCount = this.TotalInputTokenCount;
        var previousOutputTokenCount = this.TotalOutputTokenCount;
        var previousCacheReadTokenCount = this.TotalCacheReadTokenCount;
        var previousCacheWriteTokenCount = this.TotalCacheWriteTokenCount;
        var previousReasoningTokenCount = this.TotalReasoningTokenCount;
        var previousCostMicroUsd = this.TotalSessionCostMicroUsd;

        if (hasInputTokenCount)
        {
            this.TotalInputTokenCount = (this.TotalInputTokenCount ?? 0L) + inputTokenCountToAdd;
        }

        if (hasOutputTokenCount)
        {
            this.TotalOutputTokenCount = (this.TotalOutputTokenCount ?? 0L) + outputTokenCountToAdd;
        }

        if (hasCacheReadTokenCount)
        {
            this.TotalCacheReadTokenCount = (this.TotalCacheReadTokenCount ?? 0L) + cacheReadTokenCountToAdd;
        }

        if (hasCacheWriteTokenCount)
        {
            this.TotalCacheWriteTokenCount = (this.TotalCacheWriteTokenCount ?? 0L) + cacheWriteTokenCountToAdd;
        }

        if (hasReasoningTokenCount)
        {
            this.TotalReasoningTokenCount = (this.TotalReasoningTokenCount ?? 0L) + reasoningTokenCountToAdd;
        }

        if (hasCost)
        {
            this.TotalSessionCostMicroUsd = (this.TotalSessionCostMicroUsd ?? 0L) + costMicroUsdToAdd;
        }

        if (this.TotalInputTokenCount != previousInputTokenCount
            || this.TotalOutputTokenCount != previousOutputTokenCount
            || this.TotalCacheReadTokenCount != previousCacheReadTokenCount
            || this.TotalCacheWriteTokenCount != previousCacheWriteTokenCount
            || this.TotalReasoningTokenCount != previousReasoningTokenCount
            || this.TotalSessionCostMicroUsd != previousCostMicroUsd)
        {
            this.UsageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Awaits <paramref name="task"/> but returns early if <paramref name="cancellationToken"/> is
    /// canceled first. Returns true when canceled before the task completed (the task is left running
    /// and should be observed/disposed by the caller), false when the task completed first.
    /// </summary>
    private static async Task<bool> WasCanceledBeforeCompletingAsync(Task task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted)
        {
            return false;
        }

        var cancellationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancellationSignal))
        {
            var completed = await Task.WhenAny(task, cancellationSignal.Task).ConfigureAwait(false);
            return completed != task;
        }
    }

    private IAsyncEnumerable<AgentResponseUpdate> StartRun(
        ChatMessage[] messages,
        AgentChatSession session,
        CancellationToken cancellationToken)
    {
        var runOptions = this.CreateRunOptions();
        return session
            .RunStreamAsync(messages, runOptions, cancellationToken);
    }

    private ChatClientAgentRunOptions? CreateRunOptions()
    {
        var chatOptions = this.chatOptions?.ChatOptions;
        if (chatOptions is null)
        {
            return null;
        }

        return new ChatClientAgentRunOptions
        {
            ChatOptions = chatOptions,
        };
    }

    private static string ResolveAssistantTextChunk(AgentResponseUpdate update)
    {
        if (!string.IsNullOrEmpty(update.Text))
        {
            return update.Text;
        }

        return string.Concat(
            update.Contents
                .OfType<TextContent>()
                .Select(static content => content.Text));
    }

    /// <summary>
    /// Decides whether the agent framework should use the resolved chat client as-is (without adding
    /// function-invoking middleware). This is true when a client is explicitly provided (tests inject a
    /// ready-to-use client) or when the client invokes its own tools
    /// (<see cref="ISelfInvokingToolChatClient"/>, e.g. the GitHub Copilot SDK). For self-invoking
    /// clients the middleware is both unnecessary and harmful — it buffers streaming tool-call/result
    /// content so it would not stream live into the GUI.
    /// </summary>
    internal static bool ResolveUseProvidedChatClientAsIs(bool hasClientOverride, IChatClient resolvedClient)
    {
        ArgumentNullException.ThrowIfNull(resolvedClient);
        return hasClientOverride
            || resolvedClient is ISelfInvokingToolChatClient
            || resolvedClient.GetService(typeof(ISelfInvokingToolChatClient)) is not null;
    }

    private static bool IsToolContinuationFinishReason(ChatFinishReason? finishReason)
    {
        if (finishReason is null)
        {
            return false;
        }

        var finishReasonText = finishReason?.ToString() ?? string.Empty;
        return finishReasonText.Contains("tool", StringComparison.OrdinalIgnoreCase)
            || finishReasonText.Contains("function", StringComparison.OrdinalIgnoreCase);
    }

    private AgentChatSession GetSession()
    {
        lock (this.sessionLock)
        {
            return this.session ?? throw new InvalidOperationException("Agent session has not been initialized.");
        }
    }

    private void SetSession(AgentChatSession nextSession)
    {
        ArgumentNullException.ThrowIfNull(nextSession);
        lock (this.sessionLock)
        {
            this.session = nextSession;
        }

        this.historyService!.BindSession(nextSession);
    }

    private void StartProcessingLoop()
    {
        lock (this.processingStateLock)
        {
            if (this.cts.IsCancellationRequested || this.processingStarted)
            {
                return;
            }

            this.processingStarted = true;
            // Run the process loop on the same foreground scheduler used for running-item mutations.
            // In production this is the captured UI synchronization context; off the UI thread it is
            // the exclusive scheduler, which serializes the loop's running-item lifecycle operations
            // (create/update/complete) with the conflator's running-item population so the
            // collections are never mutated concurrently.
            // The loop must be invoked inside the StartNew delegate: invoking the async method
            // eagerly here would run it (and its await continuations) on the calling thread —
            // and StartNew would merely wrap the already-running task without scheduling
            // anything (issue #908). Construction is now verified to occur on the foreground
            // context (issue #909), but this explicit scheduling remains the mechanism that
            // binds the loop to the foreground scheduler: even on the UI thread,
            // TaskScheduler.Current is TaskScheduler.Default outside a scheduled task.
            this.processTask = Task.Factory.StartNew(
                () => this.acceptsUserInput
                    ? this.RunProcessLoopAsync(this.cts.Token)
                    : this.RunHostedProcessLoopAsync(this.cts.Token),
                this.cts.Token,
                TaskCreationOptions.DenyChildAttach,
                this.foregroundScheduler).Unwrap();
        }
    }

    private async Task InitializeMcpToolsAsync(CancellationToken cancellationToken = default)
    {
        var agent = this.agentDefinition;
        var client = this.client;
        var chatOptions = this.chatOptions;
        var persistenceProvider = this.persistenceProvider;

        if (agent is null || client is null || chatOptions?.ChatOptions is null || persistenceProvider is null)
        {
            return;
        }

        var agentTools = AgentFactory.ExtractTools(agent);
        if (agentTools is not { Count: > 0 })
        {
            return;
        }

        await this.toolMutationLock.WaitAsync(cancellationToken);
        try
        {
            // MCP tools and custom toolsets now share a single source of truth: the runtime
            // context provider registrations built by CreateRuntimeContextProviderRegistrationsAsync
            // (issue #1395). Dispatching from the same registration list guarantees the provider
            // that feeds the UI tool tree / diagnostic is the SAME instance already wired into
            // chatOptions.AIContextProviders, so MCP tools reach the model.
            var toolTasks = this.runtimeContextProviderRegistrations.Select(registration => registration.Tool switch
            {
                McpTool mcpTool => this.InitializeMcpRuntimeToolAsync(
                    mcpTool,
                    registration.Provider,
                    cancellationToken),
                CustomTool customTool => this.InitializeCustomToolRuntimeAsync(
                    customTool,
                    registration.Provider,
                    registration.ErrorMessage,
                    cancellationToken),
                _ => Task.FromResult(new ToolInitializationResult([], [])),
            });
            var results = await Task.WhenAll(toolTasks);
            var roots = results.SelectMany(static result => result.Roots).ToList();

            this.ReplaceToolNodes(roots);

            // Per-step running items (one per toolset / MCP server) already emit their own
            // "Loading …" -> tool-listing diagnostics into unpersisted history, so no lumped
            // "Agent ready. Loaded tools: …" summary running item is emitted here (issue #1072).
            this.ToolsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Each toolset / MCP step now captures its own failure into a per-step diagnostic that
            // names the failed step, so this only handles truly unexpected orchestration errors.
            var startupRunningItem = this.CreateRunningItem(new AgentChatHistoryItem
            {
                Role = AgentChatHistoryItem.DiagnosticChatRole,
                Contents = new AIContent[] { new ErrorContent($"Agent startup failed: {ex}") },
                Timestamp = this.timeProvider.GetUtcNow(),
            });
            this.CompleteRunningItem(startupRunningItem, true);
        }
        finally
        {
            this.toolMutationLock.Release();
        }
    }

    private async Task<IReadOnlyList<RuntimeContextProviderRegistration>> CreateRuntimeContextProviderRegistrationsAsync(
        AgentDefinition agent,
        AgentServices? services,
        CancellationToken cancellationToken)
    {
        var agentTools = AgentFactory.ExtractTools(agent);
        if (agentTools is not { Count: > 0 })
        {
            return [];
        }

        var customTools = agentTools.OfType<CustomTool>()
            .Where(tool => !string.Equals(tool.Kind, "chat-history", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var mcpTools = agentTools.OfType<McpTool>().ToArray();
        if (customTools.Length == 0 && mcpTools.Length == 0)
        {
            return [];
        }

        var toolsetFactory = services?.ToolsetFactory ?? ToolsetFactory.CreateDefaultToolsetFactory();
        var resolvedServices = services ?? new AgentServices();
        var providerTasks = customTools.Select(async tool =>
        {
            var provider = await toolsetFactory.CreateToolsetAsync(tool, resolvedServices);
            return new RuntimeContextProviderRegistration(
                tool,
                provider,
                provider is null ? $"No tool provider is mapped for kind '{tool.Kind}'." : null,
                Core.Transport.ExecutorTargetResolver.ForTool(tool));
        }).ToArray();

        var customRegistrations = await Task.WhenAll(providerTasks);

        // Construct the MCP provider ONCE here so the SAME instance both feeds the UI tool tree /
        // diagnostic (InitializeMcpRuntimeToolAsync) AND is wired into chatOptions.AIContextProviders,
        // exposing MCP tools to the model exactly like CustomTool toolsets (issue #1395). No network
        // connection happens here — McpToolContextProvider.ProvideAIContextAsync connects lazily — so
        // the registration/reference exists at construction time without blocking on the network.
        var mcpRegistrations = mcpTools.Select(tool =>
        {
            var provider = new McpToolContextProvider(
                tool,
                services?.LoggerFactory,
                Core.Transport.ExecutorTargetResolver.ForTool(tool),
                services);
            this.RegisterOwnedResource(provider);
            return new RuntimeContextProviderRegistration(
                tool,
                provider,
                null,
                Core.Transport.ExecutorTargetResolver.ForTool(tool));
        }).ToArray();

        return [.. customRegistrations, .. mcpRegistrations];
    }

    private async Task<ToolInitializationResult> InitializeCustomToolRuntimeAsync(
        CustomTool tool,
        AIContextProvider? toolset,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var kind = tool.Kind;
        if (string.IsNullOrWhiteSpace(kind))
        {
            return new ToolInitializationResult([], []);
        }

        var displayName = kind;
        var summary = tool.Description ?? string.Empty;
        var runningItem = this.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = AgentChatHistoryItem.DiagnosticChatRole,
            Contents = new AIContent[] { new TextContent($"Loading toolset {displayName}") },
            Timestamp = this.timeProvider.GetUtcNow(),
        });

        try
        {
            if (toolset is null)
            {
                var errorText = errorMessage ?? $"No tool provider is mapped for kind '{kind}'.";
                var failedNode = new ToolStateNode(
                    id: BuildCustomToolId(tool),
                    name: displayName,
                    description: summary,
                    instructions: summary,
                    kind: kind,
                    runtimeTool: null,
                    parent: null,
                    isEnabled: false,
                    status: errorText);

                this.UpdateRunningItem(runningItem, [new AgentChatHistoryItem
                {
                    Role = AgentChatHistoryItem.DiagnosticChatRole,
                    Contents = new AIContent[] { new ErrorContent(errorText) },
                    Timestamp = this.timeProvider.GetUtcNow(),
                }]);
                return new ToolInitializationResult([failedNode], []);
            }

            if (toolset is IAsyncDisposable asyncDisposable)
            {
                this.RegisterOwnedResource(asyncDisposable);
            }

            var runtimeTools = await AIContextProviderToolReader.GetToolsAsync(
                toolset,
                this.GetSession().Agent,
                this.GetSession().Session,
                cancellationToken);
            var singleRuntimeTool = runtimeTools.Length == 1 ? runtimeTools[0] : null;
            var shouldAttachSingleRuntimeToolToRoot =
                singleRuntimeTool is not null
                && string.Equals(singleRuntimeTool.Name, displayName, StringComparison.Ordinal);
            var root = new ToolStateNode(
                id: BuildCustomToolId(tool),
                name: displayName,
                description: summary,
                instructions: summary,
                kind: kind,
                runtimeTool: shouldAttachSingleRuntimeToolToRoot ? singleRuntimeTool : null,
                parent: null,
                isEnabled: true,
                status: runtimeTools.Length == 0
                    ? "Loaded no tools."
                    : $"Loaded {runtimeTools.Length} tool{(runtimeTools.Length == 1 ? string.Empty : "s")}.");

            if (!shouldAttachSingleRuntimeToolToRoot)
            {
                foreach (var runtimeTool in runtimeTools)
                {
                    var childName = string.IsNullOrWhiteSpace(runtimeTool.Name) ? "(unnamed)" : runtimeTool.Name;
                    root.Children.Add(new ToolStateNode(
                        id: BuildCustomChildToolId(tool, childName),
                        name: childName,
                        description: runtimeTool.Description ?? string.Empty,
                        instructions: runtimeTool.Description ?? string.Empty,
                        kind: "custom-tool",
                        runtimeTool: runtimeTool,
                        parent: root,
                        isEnabled: true,
                        status: null));
                }
            }

            this.UpdateRunningItem(runningItem, [new AgentChatHistoryItem
            {
                Role = AgentChatHistoryItem.DiagnosticChatRole,
                Contents = new AIContent[] { new TextContent(McpClientToolListing.BuildOpenedToolsMessage("toolset", displayName, runtimeTools)) },
                Timestamp = this.timeProvider.GetUtcNow(),
            }]);
            return new ToolInitializationResult([root], runtimeTools.ToList());
        }
        catch (Exception ex)
        {
            // Attribute a custom-toolset failure to this step (mirroring the MCP path) so the
            // exception and the failed step name land in the unpersisted history diagnostic rather
            // than being swallowed into the generic "Agent startup failed" summary (issue #1072).
            var failureMessage = $"Failed to load toolset '{displayName}': {ex}";
            this.UpdateRunningItem(runningItem, [new AgentChatHistoryItem
            {
                Role = AgentChatHistoryItem.DiagnosticChatRole,
                Contents = new AIContent[] { new ErrorContent(failureMessage) },
                Timestamp = this.timeProvider.GetUtcNow(),
            }]);

            var failedNode = new ToolStateNode(
                id: BuildCustomToolId(tool),
                name: displayName,
                description: summary,
                instructions: summary,
                kind: kind,
                runtimeTool: null,
                parent: null,
                isEnabled: false,
                status: failureMessage);
            return new ToolInitializationResult([failedNode], []);
        }
        finally
        {
            this.CompleteRunningItem(runningItem, true);
        }
    }

    private async Task<ToolInitializationResult> InitializeMcpRuntimeToolAsync(
        McpTool mcpTool,
        AIContextProvider? provider,
        CancellationToken cancellationToken)
    {
        var toolServerName = string.IsNullOrWhiteSpace(mcpTool.ServerName) ? mcpTool.Name : mcpTool.ServerName;
        var displayName = toolServerName ?? "(mcp server)";
        var runningItem = this.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = AgentChatHistoryItem.DiagnosticChatRole,
            Contents = new AIContent[] { new TextContent($"Loading mcp server {displayName}") },
            Timestamp = this.timeProvider.GetUtcNow(),
        });

        try
        {
            var serverNode = new ToolStateNode(
                id: BuildMcpServerToolId(toolServerName),
                name: displayName,
                description: mcpTool.ServerDescription ?? mcpTool.Description ?? string.Empty,
                instructions: mcpTool.ServerDescription ?? mcpTool.Description ?? string.Empty,
                kind: "mcp",
                runtimeTool: null,
                parent: null,
                isEnabled: true,
                status: null);

            if (provider is null)
            {
                throw new InvalidOperationException(
                    $"No MCP tool provider was created for server '{displayName}'.");
            }

            // Enumerate through the SAME provider instance that was registered into
            // chatOptions.AIContextProviders (issue #1395). This both builds the UI tool tree and,
            // because the provider is already wired for exposure, guarantees the "Loaded tools"
            // diagnostic below names exactly the tools the model can call — every child node is
            // indexed enabled with a matching RuntimeTool.Name so it passes IsToolEnabledForRuntime.
            var mcpTools = await AIContextProviderToolReader.GetToolsAsync(
                provider,
                this.GetSession().Agent,
                this.GetSession().Session,
                cancellationToken);

            foreach (var mcpRuntimeTool in mcpTools)
            {
                serverNode.Children.Add(new ToolStateNode(
                    id: BuildMcpChildToolId(toolServerName, mcpRuntimeTool.Name),
                    name: mcpRuntimeTool.Name,
                    description: mcpRuntimeTool.Description ?? string.Empty,
                    instructions: mcpRuntimeTool.Description ?? string.Empty,
                    kind: "mcp-tool",
                    runtimeTool: mcpRuntimeTool,
                    parent: serverNode,
                    isEnabled: true,
                    status: null));
            }

            if (mcpTools.Length == 0)
            {
                serverNode.Status = "Loaded no tools.";
            }
            else
            {
                serverNode.Status = $"Loaded {mcpTools.Length} tool{(mcpTools.Length == 1 ? string.Empty : "s")}.";
            }

            this.UpdateRunningItem(runningItem, [new AgentChatHistoryItem
            {
                Role = AgentChatHistoryItem.DiagnosticChatRole,
                Contents = new AIContent[] { new TextContent(McpClientToolListing.BuildOpenedToolsMessage("MCP server", displayName, mcpTools)) },
                Timestamp = this.timeProvider.GetUtcNow(),
            }]);
            return new ToolInitializationResult([serverNode], mcpTools.Cast<AITool>().ToList());
        }
        catch (Exception ex)
        {
            var errorMessage = $"Failed to open MCP server '{displayName}': {ex}";
            this.UpdateRunningItem(runningItem, [new AgentChatHistoryItem
            {
                Role = AgentChatHistoryItem.DiagnosticChatRole,
                Contents = new AIContent[] { new ErrorContent(errorMessage) },
                Timestamp = this.timeProvider.GetUtcNow(),
            }]);

            var failedNode = new ToolStateNode(
                id: BuildMcpServerToolId(toolServerName),
                name: displayName,
                description: mcpTool.ServerDescription ?? mcpTool.Description ?? string.Empty,
                instructions: mcpTool.ServerDescription ?? mcpTool.Description ?? string.Empty,
                kind: "mcp",
                runtimeTool: null,
                parent: null,
                isEnabled: false,
                status: errorMessage);
            return new ToolInitializationResult([failedNode], []);
        }
        finally
        {
            this.CompleteRunningItem(runningItem, true);
        }
    }

    private static string BuildCustomToolId(Tool tool)
        => $"custom:{tool.Kind}:{tool.Name}";

    private static string BuildCustomChildToolId(Tool tool, string toolName)
        => $"{BuildCustomToolId(tool)}:{toolName}";

    private static string BuildMcpServerToolId(string? serverName)
        => $"mcp:{serverName ?? "(server)"}";

    private static string BuildMcpChildToolId(string? serverName, string toolName)
        => $"{BuildMcpServerToolId(serverName)}:{toolName}";

    private void ReplaceToolNodes(IReadOnlyList<ToolStateNode> roots)
    {
        lock (this.toolsLock)
        {
            this.toolRoots.Clear();
            this.toolIndex.Clear();

            foreach (var root in roots)
            {
                this.toolRoots.Add(root);
                IndexToolNode(root, this.toolIndex);
            }
        }
    }

    private static void IndexToolNode(ToolStateNode node, IDictionary<string, ToolStateNode> index)
    {
        index[node.Id] = node;
        foreach (var child in node.Children)
        {
            IndexToolNode(child, index);
        }
    }

    private List<AITool> GetEnabledRuntimeTools()
    {
        lock (this.toolsLock)
        {
            return this.toolIndex.Values
                .Where(static node => node.IsEnabled && node.RuntimeTool is not null)
                .Select(static node => node.RuntimeTool!)
                .ToList();
        }
    }

    private bool IsToolEnabledForRuntime(AITool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            return false;
        }

        lock (this.toolsLock)
        {
            return this.toolIndex.Values.Any(node =>
                node.IsEnabled
                && node.RuntimeTool is not null
                && string.Equals(node.RuntimeTool.Name, tool.Name, StringComparison.Ordinal));
        }
    }

    private static bool SetNodeEnabled(ToolStateNode node, bool enabled)
    {
        var changed = false;
        if (node.Children.Count == 0)
        {
            if (node.IsEnabled != enabled)
            {
                node.IsEnabled = enabled;
                changed = true;
            }

            return changed;
        }

        foreach (var child in node.Children)
        {
            changed |= SetNodeEnabled(child, enabled);
        }

        if (node.IsEnabled != enabled)
        {
            node.IsEnabled = enabled;
            changed = true;
        }

        return changed;
    }

    private static void RecomputeAncestorEnabled(ToolStateNode node)
    {
        node.IsEnabled = node.Children.Count == 0 || node.Children.All(static child => child.IsEnabled);
        if (node.Parent is not null)
        {
            RecomputeAncestorEnabled(node.Parent);
        }
    }

    private static AgentChatToolItem CreateToolSnapshot(ToolStateNode node)
        => new(
            node.Id,
            node.Name,
            node.Description,
            node.Instructions,
            node.Kind,
            node.IsEnabled,
            node.Children.Select(CreateToolSnapshot).ToArray(),
            node.Status);

    private sealed class RuntimeToolsInitializationResult(
        IReadOnlyList<ToolStateNode> roots,
        IReadOnlyList<AITool> runtimeTools)
    {
        public IReadOnlyList<ToolStateNode> Roots { get; } = roots;

        public IReadOnlyList<AITool> RuntimeTools { get; } = runtimeTools;
    }

    private sealed class ToolStateNode(
        string id,
        string name,
        string description,
        string instructions,
        string kind,
        AITool? runtimeTool,
        ToolStateNode? parent,
        bool isEnabled,
        string? status)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public string Description { get; } = description;

        public string Instructions { get; } = instructions;

        public string Kind { get; } = kind;

        public AITool? RuntimeTool { get; } = runtimeTool;

        public ToolStateNode? Parent { get; } = parent;

        public bool IsEnabled { get; set; } = isEnabled;

        public string? Status { get; set; } = status;

        public List<ToolStateNode> Children { get; } = [];
    }

    private sealed record ToolInitializationResult(
        IReadOnlyList<ToolStateNode> Roots,
        IReadOnlyList<AITool> RuntimeTools);

    private sealed record RuntimeContextProviderRegistration(
        Tool Tool,
        AIContextProvider? Provider,
        string? ErrorMessage,
        Core.Transport.ExecutorTarget ExecutorTarget);

}