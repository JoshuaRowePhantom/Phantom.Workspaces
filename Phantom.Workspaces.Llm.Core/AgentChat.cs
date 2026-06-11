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
/// the current synchronization context if one is present when processing starts,
/// otherwise the default task scheduler.
/// </summary>
public sealed class AgentChat : IAsyncDisposable
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
    private AgentPersistenceChatHistoryProvider? persistenceProvider;
    private ChatClientAgent? chatClientAgent;
    private ChatClientAgentOptions? chatOptions;
    private IReadOnlyList<RuntimeContextProviderRegistration> runtimeContextProviderRegistrations = [];
    private readonly AgentInputQueueManager queueManager;
    private readonly AgentChatQueueManager chatQueueManager;
    private AgentChatHistoryService? historyService;
    private readonly AgentChatHistoryCollection history = new();
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
    private Task processTask = Task.CompletedTask;
    private string agentSessionId = Guid.NewGuid().ToString("n");

    private bool isBusy;
    private bool processingStarted;
    private readonly object processingStateLock = new();
    private CancellationTokenSource? activeRunCancellation;
    private int disposeStarted;

    internal AgentChat(InternalCreateAgentChatRequest request)
    {
       this.request = request;
       this.queueManager = new AgentInputQueueManager();
       this.chatQueueManager = new AgentChatQueueManager(this.queueManager);
       this.runningItemOperations = new AgentRunningItems(this.runningItems);
       this.ownedResources = request.OwnedResources?.ToList() ?? [];
       this.PendingApprovalItems = new ReadOnlyObservableCollection<AgentChatPendingApprovalItem>(this.pendingApprovalItems);
    }

    internal static async Task<AgentChat> CreateAsync(InternalCreateAgentChatRequest request)
    {
       var chat = new AgentChat(request);
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
       var clientInfo = this.request.ClientOverride is not null
           ? (this.request.ClientOverride, this.request.DisplayNameOverride ?? string.Empty)
           : AgentFactory.CreateChatClient(resolvedAgentDefinition, this.request.AgentServices);
       var resolvedClient = clientInfo.Item1;
       if (this.request.AgentServices?.LogChat == true)
       {
           resolvedClient = resolvedClient.AsBuilder().UseLogging(this.request.AgentServices.LoggerFactory).Build();
       }

       this.client = resolvedClient;
       this.DisplayName = this.request.DisplayNameOverride ?? clientInfo.Item2;

       this.persistenceProvider = new AgentPersistenceChatHistoryProvider(resolvedAgentDefinition, this.request.ConfiguredStore);
       this.chatHistoryProvider = new AgentFrameworkChatHistoryProvider(this.persistenceProvider);
       this.historyService = new AgentChatHistoryService(this.History, this.chatHistoryProvider);
       this.chatOptions = new ChatClientAgentOptions
       {
           ChatOptions = new ChatOptions(),
           ChatHistoryProvider = this.chatHistoryProvider,
           UseProvidedChatClientAsIs = this.request.ClientOverride is not null,
       };
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

       this.chatClientAgent = new ChatClientAgent(resolvedClient, this.chatOptions);
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

       var resolvedAgentSessionId = this.persistenceProvider.ExtractAgentSessionId(frameworkSession);
       var persistedMessages = await this.request.ConfiguredStore.ReadMessagesAsync(
           new ReadMessagesRequest { AgentSessionId = resolvedAgentSessionId },
           this.request.CancellationToken);

       this.LoadInitialHistory(persistedMessages);
       this.SetSession(new AgentChatSession(this.chatClientAgent, frameworkSession));
       this.SetAgentSessionId(resolvedAgentSessionId);
       this.StartProcessingLoop();

       await this.InitializeMcpToolsAsync(this.request.CancellationToken);
    }

    /// <summary>
    /// Fired when the active streaming turn finishes.
    /// The argument is the completed <see cref="AgentChatHistoryItem"/> that was added to <see cref="History"/>.
    /// Fires on the background processing thread.
    /// </summary>
    public event EventHandler<AgentChatHistoryItem>? TurnCompleted;

    public event EventHandler<string>? AgentSessionIdChanged;

    public event EventHandler? ToolsChanged;

    /// <summary>Completed conversation turns, in order.</summary>
    public AgentChatHistoryCollection History => this.history;

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

    public string DisplayName { get; private set; } = string.Empty;

    public string AgentSessionId => this.agentSessionId;

    public IReadOnlyList<AgentChatToolItem> Tools => this.GetToolSnapshot();

    public IReadOnlyList<AgentChatToolItem> GetToolSnapshot()
    {
        lock (this.toolsLock)
        {
            return this.toolRoots.Select(CreateToolSnapshot).ToArray();
        }
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
                    Messages = [new ChatMessage(ChatRole.User, contents.ToList())],
                },
            ]);
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
            foreach (var historyItem in item.Items)
            {
                this.AddHistoryItem(historyItem);
                this.TurnCompleted?.Invoke(this, historyItem);
            }
        }

        this.runningItemOperations.Remove(item);
    }

    private void AddHistoryItem(AgentChatHistoryItem item)
    {
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
            };

            this.History.Add(nextItem);
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
    }

    private async Task UpdateCurrentPartialResponse(
        AgentChatRunningItem currentRunningItem,
        AgentResponseUpdate agentResponseUpdate,
        List<AgentResponseUpdate> agentResponseUpdates)
    {
        agentResponseUpdates.Add(agentResponseUpdate);

        var chatResponseUpdates = agentResponseUpdates.ToAsyncEnumerable().AsChatResponseUpdatesAsync();
        var chatResponse = await chatResponseUpdates.ToChatResponseAsync();

        bool lastIsToolResult = Enumerable.OfType<ToolResultContent>(agentResponseUpdate.Contents).Any();

        IEnumerable<AgentChatHistoryItem> finalItem = Array.Empty<AgentChatHistoryItem>();
        if (lastIsToolResult)
        {
            finalItem = new AgentChatHistoryItem[]
            {
                new AgentChatHistoryItem
                {
                    Role = ChatRole.Assistant,
                }
            };
        }

        var chatHistoryItems = chatResponse.Messages.Reverse().Select((message, index) => new AgentChatHistoryItem
        {
            Role = message.Role,
            Contents = message.Contents.ToArray(),
        }).Reverse().Concat(finalItem).ToArray();

        this.UpdateRunningItem(currentRunningItem, chatHistoryItems);
    }

    private async Task RunProcessLoopAsync(
        CancellationToken cancellationToken)
    {
        lock (processingStateLock)
        {
            activeRunCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }
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
                    }]);

                try
                {
                    List<AgentResponseUpdate> agentResponseUpdates = new List<AgentResponseUpdate>();

                    await foreach (var update in this.StartRun(
                        chatMessagesToSubmit.ToArray(),
                        currentSession,
                        cancellationToken))
                    {
                        await this.UpdateCurrentPartialResponse(
                            currentPartialTextResponseItem,
                            update,
                            agentResponseUpdates);
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    var runningItem = currentPartialTextResponseItem
                        ?? throw new InvalidOperationException("Running item was unexpectedly null while handling a provider error.");
                    var errorItems = runningItem.Items
                        .Concat([
                            new AgentChatHistoryItem
                            {
                                Role = ChatRole.Assistant,
                                Contents = [new ErrorContent($"Provider error: {ex.Message}")],
                            },
                        ])
                        .ToArray();

                    this.UpdateRunningItem(runningItem, errorItems);
                }
                finally
                {
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
            var scheduler = SynchronizationContext.Current is not null
                ? TaskScheduler.FromCurrentSynchronizationContext()
                : TaskScheduler.Default;
            this.processTask = Task.Factory.StartNew(
                () => this.RunProcessLoopAsync(this.cts.Token),
                this.cts.Token,
                TaskCreationOptions.DenyChildAttach,
                scheduler).Unwrap();
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
            var summaryRunningItem = this.CreateRunningItem(new AgentChatHistoryItem
            {
                Role = AgentChatHistoryItem.DiagnosticChatRole,
                Contents = new AIContent[] { new TextContent(BuildStartupReadyMessage(this.GetEnabledRuntimeTools())) },
            });
            this.CompleteRunningItem(summaryRunningItem, true);
            this.ToolsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            var startupRunningItem = this.CreateRunningItem(new AgentChatHistoryItem
            {
                Role = AgentChatHistoryItem.DiagnosticChatRole,
                Contents = new AIContent[] { new ErrorContent($"Agent startup failed: {ex.Message}") },
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
                provider is null ? $"No tool provider is mapped for kind '{tool.Kind}'." : null);
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
            Contents = new AIContent[] { new TextContent($"Initializing toolset '{displayName}'...") },
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
                Contents = new AIContent[] { new TextContent($"Opened toolset '{displayName}' ({runtimeTools.Length} tools).") },
            }]);
            return new ToolInitializationResult([root], runtimeTools.ToList());
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
            Contents = new AIContent[] { new TextContent($"Initializing MCP server '{displayName}'...") },
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

            var provider = new McpToolContextProvider(mcpTool, services?.LoggerFactory);
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
                Contents = new AIContent[] { new TextContent($"Opened MCP server '{displayName}' ({mcpTools.Length} tools).") },
            }]);
            return new ToolInitializationResult([serverNode], mcpTools.Cast<AITool>().ToList());
        }
        catch (Exception ex)
        {
            var errorMessage = $"Failed to open MCP server '{displayName}': {ex.Message}";
            this.UpdateRunningItem(runningItem, [new AgentChatHistoryItem
            {
                Role = AgentChatHistoryItem.DiagnosticChatRole,
                Contents = new AIContent[] { new ErrorContent(errorMessage) },
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

    private static string BuildStartupReadyMessage(IReadOnlyList<AITool> runtimeTools)
    {
        if (runtimeTools.Count == 0)
        {
            return "Agent ready. Loaded tools: (none).";
        }

        var toolNames = runtimeTools
            .Select(tool => tool.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (toolNames.Length == 0)
        {
            return "Agent ready. Loaded tools: (unnamed tools).";
        }

        return $"Agent ready. Loaded tools:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", toolNames)}";
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
        string? ErrorMessage);

}
