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
    private AgentChatSession? session;
    private AgentDefinition? agentDefinition;
    private IChatClient? client;
    private AgentFrameworkChatHistoryProvider? chatHistoryProvider;
    private IncrementalPersistenceChatHistoryProvider? persistenceProvider;
    private ChatClientAgent? chatClientAgent;
    private ChatClientAgentOptions? chatOptions;
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
    private DateTime lastUpdatedAt = DateTime.UtcNow;
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

       var resolvedAgentDefinition = this.request.AgentDefinition
           ?? (restoredAgentDefinitionJson is not null
               ? AgentDefinition.FromJson(restoredAgentDefinitionJson.ToJson())
               : null);
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
           copilotChatClient.SetSubAgentDependencies(
               this.request.AgentServices?.RunningAgentChatFactory as IRunningAgentChatFactory,
               this);
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
       this.historyService = new AgentChatHistoryService(this.History, this.chatHistoryProvider);
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
       // established (issue #3).
       if (resolvedClient.GetService(typeof(CopilotSdkChatClient)) is CopilotSdkChatClient copilotSdkClient)
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

       // Session initialization runs inline (not dispatched) so the session is fully established
       // when CreateAsync returns, preserving the historical contract that callers — and tests that
       // supply a foreground scheduler which only executes when externally pumped — depend on.
       // CreateAsync already executes on the foreground context (enforced by the constructor for a
       // SynchronizationContextTaskScheduler), so these running-item mutations are on the foreground
       // scheduler, and the session-init running item is fully completed before the processing loop
       // starts, so it never races the loop (issues #1068 / #1072).
       var sessionInitItem = this.CreateRunningItem(new AgentChatHistoryItem
       {
           Role = AgentChatHistoryItem.DiagnosticChatRole,
           Contents = new AIContent[] { new TextContent("Loading session") },
           Timestamp = DateTimeOffset.UtcNow,
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
               Timestamp = DateTimeOffset.UtcNow,
           }]);
           this.CompleteRunningItem(sessionInitItem, writeToHistory: true);
           throw;
       }

       this.StartProcessingLoop();

       // Tool initialization mutates running items (one per toolset / MCP server) and must be
       // serialized with the processing loop on the foreground scheduler (issue #1068). Only
       // dispatch when there is actual tool work: a tool-less agent performs no running-item
       // mutations, so skipping the dispatch keeps CreateAsync from blocking on a foreground
       // scheduler that defers execution until externally pumped (e.g. sub-agent restore tests).
       var runtimeTools = AgentFactory.ExtractTools(resolvedAgentDefinition);
       var hasToolWork = this.runtimeContextProviderRegistrations.Count > 0
           || (runtimeTools?.OfType<McpTool>().Any() ?? false);
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
    /// Raised when a slash command or the host wants to display a one-off status message
    /// in the chat area without persisting it to conversation history.
    /// </summary>
    public event EventHandler<string>? TransientNotification;
    
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
    public string AgentId => this.agentId;

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
                            CreatedAt = DateTimeOffset.UtcNow,
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
                    Timestamp = DateTimeOffset.UtcNow,
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
                    Timestamp = DateTimeOffset.UtcNow,
                });
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            this.foregroundScheduler);
    }

    /// <summary>
    /// Fires <see cref="TransientNotification"/> without touching <see cref="History"/>.
    /// Used for slash-command status messages that should be shown as one-off inline notifications.
    /// </summary>
    public void RaiseTransientNotification(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        this.TransientNotification?.Invoke(this, text);
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
        this.lastUpdatedAt = DateTime.UtcNow;
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
                Timestamp = DateTimeOffset.UtcNow,
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
                Timestamp = DateTimeOffset.UtcNow,
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

        var chatClient = new SubAgentChatClient(agentId, subAgentDefinition.Name ?? agentId, subAgentDefinition.Description ?? string.Empty);

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
    {
        this.completionStateOverride = state;
    }

    private async Task RestoreSubAgentsAsync(CancellationToken cancellationToken)
    {
        var childIds = await this.request.ConfiguredStore.ReadSubAgentChildIdsAsync(
            this.agentSessionId, cancellationToken);

        var factory = this.request.AgentServices?.RunningAgentChatFactory as IRunningAgentChatFactory;
        if (factory is null)
        {
            if (childIds.Count > 0)
            {
                var logger = this.request.AgentServices?.LoggerFactory?.CreateLogger<AgentChat>();
                logger?.LogWarning(
                    "Cannot restore {Count} subagent(s): IRunningAgentChatFactory unavailable",
                    childIds.Count);
            }
            return;
        }

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
        }
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
                var chatHistoryItems = await Task.Run(() => CoalesceAsync(snapshot, previousItems, interstitialSnapshot));

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
            (int AfterUpdateCount, AgentChatHistoryItem Item)[] interstitials)
        {
            AgentChatHistoryItem[] newItems;

            if (interstitials.Length == 0)
            {
                // Fast path: no interstitials, single pass.
                newItems = await CoalesceSegmentAsync(snapshot);
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
                        result.AddRange(await CoalesceSegmentAsync(snapshot[segStart..segEnd]));
                    }

                    result.Add(item);
                    segStart = segEnd;
                }

                // Tail: remaining updates after the last interstitial.
                if (segStart < snapshot.Length)
                {
                    result.AddRange(await CoalesceSegmentAsync(snapshot[segStart..]));
                }

                newItems = result.ToArray();
            }

            // Add a blank assistant placeholder if the full snapshot ends with a tool result,
            // indicating the agent is still waiting to respond to the tool output.
            var lastIsToolResult = snapshot.Length > 0
                && snapshot[^1].Contents.OfType<ToolResultContent>().Any();
            if (lastIsToolResult)
            {
                newItems = [..newItems, new AgentChatHistoryItem { Role = ChatRole.Assistant, Timestamp = DateTimeOffset.UtcNow }];
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
        private static async Task<AgentChatHistoryItem[]> CoalesceSegmentAsync(AgentResponseUpdate[] updates)
        {
            if (updates.Length == 0)
            {
                return [];
            }

            var chatResponseUpdates = updates.ToAsyncEnumerable().AsChatResponseUpdatesAsync();
            var chatResponse = await chatResponseUpdates.ToChatResponseAsync().ConfigureAwait(false);

            return chatResponse.Messages
                .Select(message => new AgentChatHistoryItem
                {
                    Role = message.Role,
                    Contents = message.Contents.ToArray(),
                    Timestamp = message.CreatedAt ?? chatResponse.CreatedAt ?? DateTimeOffset.UtcNow,
                })
                .ToArray();
        }

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
                        Timestamp = DateTimeOffset.UtcNow,
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
                                Timestamp = DateTimeOffset.UtcNow,
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
                                Timestamp = DateTimeOffset.UtcNow,
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
                    Timestamp = DateTimeOffset.UtcNow,
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
                            Timestamp = DateTimeOffset.UtcNow,
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
        var hasInputTokenCount = false;
        var hasOutputTokenCount = false;

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
        }

        var previousInputTokenCount = this.TotalInputTokenCount;
        var previousOutputTokenCount = this.TotalOutputTokenCount;

        if (hasInputTokenCount)
        {
            this.TotalInputTokenCount = (this.TotalInputTokenCount ?? 0L) + inputTokenCountToAdd;
        }

        if (hasOutputTokenCount)
        {
            this.TotalOutputTokenCount = (this.TotalOutputTokenCount ?? 0L) + outputTokenCountToAdd;
        }

        if (this.TotalInputTokenCount != previousInputTokenCount
            || this.TotalOutputTokenCount != previousOutputTokenCount)
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
            var customToolTasks = this.runtimeContextProviderRegistrations.Select(registration => this.InitializeCustomToolRuntimeAsync(
                registration.Tool,
                registration.Provider,
                registration.ErrorMessage,
                cancellationToken));
            var mcpToolTasks = agentTools.OfType<McpTool>().Select(tool => this.InitializeMcpRuntimeToolAsync(
                tool,
                this.request.AgentServices,
                cancellationToken));
            var results = await Task.WhenAll(customToolTasks.Concat(mcpToolTasks));
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
                Timestamp = DateTimeOffset.UtcNow,
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
        if (customTools.Length == 0)
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

        var registrations = await Task.WhenAll(providerTasks);
        return registrations;
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
            Timestamp = DateTimeOffset.UtcNow,
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
                    Timestamp = DateTimeOffset.UtcNow,
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
                Timestamp = DateTimeOffset.UtcNow,
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
                Timestamp = DateTimeOffset.UtcNow,
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
        AgentServices? services,
        CancellationToken cancellationToken)
    {
        var toolServerName = string.IsNullOrWhiteSpace(mcpTool.ServerName) ? mcpTool.Name : mcpTool.ServerName;
        var displayName = toolServerName ?? "(mcp server)";
        var runningItem = this.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = AgentChatHistoryItem.DiagnosticChatRole,
            Contents = new AIContent[] { new TextContent($"Loading mcp server {displayName}") },
            Timestamp = DateTimeOffset.UtcNow,
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

            var provider = new McpToolContextProvider(
                mcpTool,
                services?.LoggerFactory,
                Core.Transport.ExecutorTargetResolver.ForTool(mcpTool));
            this.RegisterOwnedResource(provider);

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
                Timestamp = DateTimeOffset.UtcNow,
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
                Timestamp = DateTimeOffset.UtcNow,
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

    private static IClientTransport CreateMcpTransport(
        McpTool tool,
        AgentServices? services)
    {
        return tool.Connection switch
        {
            AnonymousConnection anonymous => CreateTransportFromEndpoint(
                anonymous.Endpoint,
                apiKey: null,
                tool.ServerName,
                services?.LoggerFactory),
            ApiKeyConnection apiKey => CreateTransportFromEndpoint(
                apiKey.Endpoint,
                AgentFactory.ResolveApiKey(apiKey.ApiKey, tool.ServerName),
                tool.ServerName,
                services?.LoggerFactory),
            null => throw new InvalidOperationException($"MCP tool '{tool.Name}' must define a connection."),
            _ => throw new InvalidOperationException(
                $"MCP tool '{tool.Name}' has unsupported connection type '{tool.Connection.GetType().Name}'."),
        };
    }

    private static IClientTransport CreateTransportFromEndpoint(
        string? endpoint,
        string? apiKey,
        string? serverName,
        ILoggerFactory? loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("MCP tool endpoint is required.");
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new InvalidOperationException($"MCP tool endpoint '{endpoint}' is not a valid absolute URI.");
        }

        if (string.Equals(endpointUri.Scheme, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("MCP stdio transport does not support API key headers.");
            }

            return CreateStdioTransport(endpointUri, serverName);
        }

        return CreateHttpTransport(endpointUri, apiKey, serverName, loggerFactory);
    }

    private static IClientTransport CreateStdioTransport(Uri endpointUri, string? serverName)
    {
        var query = ParseUriQuery(endpointUri.Query);
        var command = GetFirstNonEmptyValue(query, "command")
            ?? (!string.IsNullOrWhiteSpace(endpointUri.Host) ? endpointUri.Host : null);
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException(
                "MCP stdio endpoint requires a command. Use stdio://?command=<process>.");
        }

        var options = new StdioClientTransportOptions
        {
            Command = command,
        };

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            options.Name = serverName;
        }

        var argValues = GetAllValues(query, "arg");
        if (argValues.Count > 0)
        {
            options.Arguments = [.. argValues];
        }

        var workingDirectory = GetFirstNonEmptyValue(query, "cwd");
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            options.WorkingDirectory = workingDirectory;
        }

        return new StdioClientTransport(options);
    }

    private static IClientTransport CreateHttpTransport(
        Uri endpointUri,
        string? apiKey,
        string? serverName,
        ILoggerFactory? loggerFactory)
    {
        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = endpointUri,
        };

        if (!string.IsNullOrWhiteSpace(serverName))
        {
            transportOptions.Name = serverName;
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            transportOptions.AdditionalHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = $"Bearer {apiKey}",
            };
        }

        return new HttpClientTransport(transportOptions, loggerFactory);
    }

    private static Dictionary<string, List<string>> ParseUriQuery(string query)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return values;
        }

        var segments = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            var separatorIndex = segment.IndexOf('=');
            var encodedKey = separatorIndex >= 0 ? segment[..separatorIndex] : segment;
            var encodedValue = separatorIndex >= 0 ? segment[(separatorIndex + 1)..] : string.Empty;

            var key = Uri.UnescapeDataString(encodedKey);
            var value = Uri.UnescapeDataString(encodedValue);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!values.TryGetValue(key, out var list))
            {
                list = [];
                values[key] = list;
            }

            list.Add(value);
        }

        return values;
    }

    private static string? GetFirstNonEmptyValue(
        IReadOnlyDictionary<string, List<string>> values,
        string key)
    {
        if (!values.TryGetValue(key, out var candidates))
        {
            return null;
        }

        return candidates.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static IReadOnlyList<string> GetAllValues(
        IReadOnlyDictionary<string, List<string>> values,
        string key)
    {
        if (!values.TryGetValue(key, out var candidates))
        {
            return [];
        }

        return candidates.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
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
        CustomTool Tool,
        AIContextProvider? Provider,
        string? ErrorMessage,
        Core.Transport.ExecutorTarget ExecutorTarget);

}
