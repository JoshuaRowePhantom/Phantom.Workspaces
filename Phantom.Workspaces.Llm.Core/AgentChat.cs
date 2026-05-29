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
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// A single chat history entry (user or completed assistant turn).
/// </summary>
public sealed record AgentChatHistoryItem
{
    public static ChatRole DiagnosticChatRole { get; } = new("diagnostic");

    public ChatRole Role { get; init; }

    /// <summary>Structured content blocks for this turn.</summary>
    public IReadOnlyList<AIContent> Contents { get; init; } = [];

    public string Text => string.Concat(this.Contents.Select(FormatContentAsText));

    public string ReasoningText => string.Concat(
        this.Contents.OfType<TextReasoningContent>().Select(static content => content.Text));

    /// <summary>True while this assistant item is still pending or streaming.</summary>
    public bool IsInProgress { get; init; }

    public bool HasText => !string.IsNullOrWhiteSpace(this.Text);

    private static string FormatContentAsText(AIContent content) => content switch
    {
        TextContent textContent => textContent.Text,
        TextReasoningContent => string.Empty,
        ToolCallContent => string.Empty,
        ToolResultContent => string.Empty,
        DataContent dataContent when !string.IsNullOrWhiteSpace(dataContent.MediaType) => $"[{dataContent.MediaType}]",
        DataContent => "[data]",
        UriContent uriContent when !string.IsNullOrWhiteSpace(uriContent.MediaType) => $"[{uriContent.MediaType}] {uriContent.Uri}",
        UriContent uriContent => uriContent.Uri.ToString(),
        _ => $"[{content.GetType().Name}]",
    };
}

/// <summary>
/// A currently-running item with model payload for GUI data templates.
/// </summary>
public sealed class AgentChatRunningItem
{
    public AgentChatHistoryItem[]? Items { get; set; }
}

/// <summary>
/// Placeholder for items awaiting approval.
/// </summary>
public sealed class AgentChatPendingApprovalItem
{
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// The default queue abstraction for the chat UI.
/// </summary>
public sealed class AgentChatQueue
{
    internal AgentChatQueue(AgentInputQueue queue, string name, bool isDefault, bool isImmediate = false)
    {
        this.Queue = queue;
        this.Name = name;
        this.IsDefault = isDefault;
        this.IsImmediate = isImmediate;
        this.Queue.Changed += this.OnQueueChanged;
    }

    internal AgentInputQueue Queue { get; }

    public string Name { get; }

    public bool IsDefault { get; }

    public bool IsImmediate { get; }

    public bool IsHeld => this.Queue.Immediacy == AgentInputQueueImmediacy.Held;

    public AgentInputQueueImmediacy Immediacy => this.Queue.Immediacy;

    public IReadOnlyList<AgentInputItem> Items => this.Queue.Items;

    public event EventHandler? Changed;

    private void OnQueueChanged(object? sender, EventArgs e) => this.Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Core session model for an agent conversation.
/// Owns the <see cref="AgentInputQueueManager"/> and drives the processing loop.
/// Exposes observable collections and events that consumers (e.g. ViewModels, CLI) can
/// subscribe to and marshal onto their own thread as needed.
/// All events and collection mutations fire on the background processing thread.
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
    private readonly AgentInputQueueManager queueManager;
    private readonly AgentChatQueueManager chatQueueManager;
    private AgentChatHistoryService? historyService;
    private readonly AgentRunningItems runningItems;
    private readonly List<IAsyncDisposable> ownedResources;
    private readonly object ownedResourcesLock = new();
    private readonly CancellationTokenSource cts = new();
    private Task processTask = Task.CompletedTask;
    private string agentSessionId = Guid.NewGuid().ToString("n");

    private bool isBusy;
    private bool processingStarted;
    private readonly object processingStateLock = new();
    private CancellationTokenSource? activeRunCancellation;

    internal AgentChat(InternalCreateAgentChatRequest request)
    {
       this.request = request;
       this.queueManager = new AgentInputQueueManager();
       this.chatQueueManager = new AgentChatQueueManager(this.queueManager);
       this.runningItems = new AgentRunningItems(this.RunningItems);
       this.runningItems.Idle += this.OnRunningItemsIdle;
       this.ownedResources = request.OwnedResources?.ToList() ?? [];
    }

    internal static async Task<AgentChat> CreateAsync(InternalCreateAgentChatRequest request)
    {
       var chat = new AgentChat(request);
       await chat.InitializeAsync().ConfigureAwait(false);
       return chat;
    }

    internal sealed record InternalCreateAgentChatRequest
    {
       public required AgentDefinition? AgentDefinition { get; init; }

       public string? AgentSessionId { get; init; }

       public AgentServices? AgentServices { get; init; }

       public required IAgentPersistenceStore ConfiguredStore { get; init; }

       public IChatClient? ClientOverride { get; init; }

       public string? DisplayNameOverride { get; init; }

       public IReadOnlyList<IAsyncDisposable>? OwnedResources { get; init; }

       public CancellationToken CancellationToken { get; init; } = default;
    }

    internal static (IChatClient client, string displayName) CreateChatClient(
       AgentDefinition agent,
       AgentServices? services = null)
    {
       var model = (agent as PromptAgent)?.Model;
       if (model is null || string.IsNullOrEmpty(model.Id))
       {
           throw new InvalidOperationException("Agent definition does not specify a model ID.");
       }

       if (string.Equals(model.Id, "test", StringComparison.OrdinalIgnoreCase))
       {
           return (new TestProviderChatClient(), "Test Chat Client");
       }

       var provider = model.Provider?.ToLowerInvariant() ?? "unknown";
       return provider switch
       {
           "echo" => (new EchoChatClient(), "Echo Chat Client"),
           "github" => CreateGitHubModelsClient(model),
           "ollama" => CreateOllamaClient(model, services),
           "openai" => throw new NotImplementedException("OpenAI provider resolution not yet implemented."),
           "azure" => throw new NotImplementedException("Azure provider resolution not yet implemented."),
           _ => throw new InvalidOperationException(
               $"Unknown or unsupported provider: {provider}. Supported: echo, test, github, ollama, openai, azure"),
       };
    }

    private static (IChatClient client, string displayName) CreateOllamaClient(Model model, AgentServices? services)
    {
       var connection = model.Connection as AgentSchema.AnonymousConnection
           ?? throw new InvalidOperationException("Ollama model requires an AnonymousConnection.");

       var endpoint = connection.Endpoint
           ?? throw new InvalidOperationException("Ollama connection requires an endpoint URL.");

       var modelId = model.Id ?? "mistral";

       try
       {
           IChatClient client;
           if (services?.LogHttpRequests == true)
           {
               var logger = services.LoggerFactory!.CreateLogger<HttpRequestLoggingHandler>();
               var handler = new HttpRequestLoggingHandler(logger)
               {
                   InnerHandler = new HttpClientHandler(),
               };
               var httpClient = new HttpClient(handler)
               {
                   BaseAddress = new Uri(endpoint),
               };
               client = new OllamaApiClient(httpClient, modelId, jsonSerializerContext: null);
           }
           else
           {
               client = new OllamaApiClient(new Uri(endpoint), modelId);
           }

           var displayName = $"Ollama ({modelId} at {endpoint})";
           return (client, displayName);
       }
       catch (Exception ex)
       {
           throw new InvalidOperationException(
               $"Failed to create Ollama client for model '{modelId}' at '{endpoint}': {ex.Message}", ex);
       }
    }

    private static (IChatClient client, string displayName) CreateGitHubModelsClient(Model model)
    {
       var connection = model.Connection as ApiKeyConnection
           ?? throw new InvalidOperationException("GitHub provider requires an ApiKeyConnection.");

       var endpoint = string.IsNullOrWhiteSpace(connection.Endpoint)
           ? GitHubModelsInferenceEndpoint
           : connection.Endpoint;

       var apiKey = ResolveApiKey(connection.ApiKey, "github-models");
       var modelId = model.Id
           ?? throw new InvalidOperationException("GitHub provider requires a model id.");

       try
       {
           var openAiClient = new OpenAIClient(
               new ApiKeyCredential(apiKey),
               new OpenAIClientOptions
               {
                   Endpoint = new Uri(endpoint, UriKind.Absolute),
               });

           IChatClient client = openAiClient.GetChatClient(modelId).AsIChatClient();
           var displayName = $"GitHub Models ({modelId} at {endpoint})";
           return (client, displayName);
       }
       catch (Exception ex)
       {
           throw new InvalidOperationException(
               $"Failed to create GitHub Models client for model '{modelId}' at '{endpoint}': {ex.Message}",
               ex);
       }
    }

    private static string ResolveApiKey(
       string? apiKeyValue,
       string? serverName)
    {
       if (string.IsNullOrWhiteSpace(apiKeyValue))
       {
           throw new InvalidOperationException($"MCP tool '{serverName ?? "unknown"}' API key is required.");
       }

       var trimmed = apiKeyValue.Trim();
       if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
       {
           var envVarName = trimmed[2..^1];
           if (string.IsNullOrWhiteSpace(envVarName))
           {
               throw new InvalidOperationException("MCP API key environment variable name cannot be empty.");
           }

           var envValue = Environment.GetEnvironmentVariable(envVarName);
           if (string.IsNullOrWhiteSpace(envValue))
           {
               if (string.Equals(envVarName, "GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase))
               {
                   var githubCliToken = ResolveGithubTokenFromCli();
                   if (!string.IsNullOrWhiteSpace(githubCliToken))
                   {
                       return githubCliToken;
                   }
               }

               throw new InvalidOperationException(
                   $"Environment variable '{envVarName}' for MCP tool '{serverName ?? "unknown"}' was not found or is empty.");
           }

           return envValue;
       }

       return trimmed;
    }

    private static string? ResolveGithubTokenFromCli()
    {
       Process? process;
       try
       {
           process = Process.Start(new ProcessStartInfo
           {
               FileName = "gh",
               Arguments = "auth token",
               RedirectStandardOutput = true,
               RedirectStandardError = true,
               UseShellExecute = false,
               CreateNoWindow = true,
           });
       }
       catch (Win32Exception)
       {
           return null;
       }

       if (process is null)
       {
           return null;
       }

       using (process)
       {
           if (!process.WaitForExit(10000))
           {
               process.Kill(entireProcessTree: true);
               throw new InvalidOperationException("Timed out while resolving GITHUB_TOKEN via 'gh auth token'.");
           }

           if (process.ExitCode != 0)
           {
               return null;
           }

           return process.StandardOutput.ReadToEnd().Trim();
       }
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
               this.request.CancellationToken).ConfigureAwait(false);
       }

       var restoredAgentDefinitionJson = restoredAgent.HasValue ? restoredAgent.Value.AgentDefinitionJson : null;
       var restoredAgentSessionJson = restoredAgent.HasValue ? restoredAgent.Value.AgentSessionJson : null;

       var resolvedAgentDefinition = restoredAgentDefinitionJson is not null
           ? AgentDefinition.FromJson(restoredAgentDefinitionJson.ToJson())
           : this.request.AgentDefinition;
       if (resolvedAgentDefinition is null)
       {
           throw new InvalidOperationException("Agent definition could not be resolved from request or persistence store.");
       }

       this.agentDefinition = resolvedAgentDefinition;
       var clientInfo = this.request.ClientOverride is not null
           ? (this.request.ClientOverride, this.request.DisplayNameOverride ?? string.Empty)
           : CreateChatClient(resolvedAgentDefinition, this.request.AgentServices);
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

       this.chatClientAgent = new ChatClientAgent(resolvedClient, this.chatOptions);
       this.persistenceProvider.SetSessionSerializer(
           async (session, token) =>
           {
               var serializedSession = await this.chatClientAgent.SerializeSessionAsync(
                   session,
                   cancellationToken: token).ConfigureAwait(false);
               return serializedSession.ToBsonDocument();
           });

       var frameworkSession = restoredAgentSessionJson is not null
           ? await this.chatClientAgent.DeserializeSessionAsync(
               restoredAgentSessionJson.ToJsonElement()
                   ?? throw new InvalidOperationException("Stored agent session JSON could not be read."),
               cancellationToken: this.request.CancellationToken).ConfigureAwait(false)
           : await this.chatClientAgent.CreateSessionAsync(this.request.CancellationToken).ConfigureAwait(false);

       if (!string.IsNullOrWhiteSpace(this.request.AgentSessionId))
       {
           this.persistenceProvider.SetAgentSessionId(frameworkSession, this.request.AgentSessionId);
       }

       var resolvedAgentSessionId = this.persistenceProvider.ExtractAgentSessionId(frameworkSession);
       var persistedMessages = await this.request.ConfiguredStore.ReadMessagesAsync(
           new ReadMessagesRequest { AgentSessionId = resolvedAgentSessionId },
           this.request.CancellationToken).ConfigureAwait(false);

       this.LoadInitialHistory(persistedMessages);
       this.SetSession(new AgentChatSession(this.chatClientAgent, frameworkSession));
       this.SetAgentSessionId(resolvedAgentSessionId);
       this.StartProcessingLoop();

       await this.InitializeMcpToolsAsync(this.request.CancellationToken).ConfigureAwait(false);
    }

    private async Task ReinitializeSessionAsync(
       AgentChatSession nextSession,
       string agentSessionId,
       CancellationToken cancellationToken)
    {
       await Task.Yield();
       this.SetSession(nextSession);
       this.SetAgentSessionId(agentSessionId);
       await Task.CompletedTask;
    }

    /// <summary>
    /// Fired when the active streaming turn finishes.
    /// The argument is the completed <see cref="AgentChatHistoryItem"/> that was added to <see cref="History"/>.
    /// Fires on the background processing thread.
    /// </summary>
    public event EventHandler<AgentChatHistoryItem>? TurnCompleted;

    public event EventHandler? Idle;

    public event EventHandler<string>? AgentSessionIdChanged;

    /// <summary>Completed conversation turns, in order.</summary>
    public ObservableCollection<AgentChatHistoryItem> History { get; } = [];

    /// <summary>Currently executing agent response items.</summary>
    public ObservableCollection<AgentChatRunningItem> RunningItems { get; } = [];

    /// <summary>All known input queues, including the default queue.</summary>
    public ObservableCollection<AgentChatQueue> InputQueues => this.chatQueueManager.InputQueues;

    /// <summary>The default input queue.</summary>
    public AgentChatQueue DefaultInputQueue => this.chatQueueManager.DefaultInputQueue;

    /// <summary>System queue that bypasses queued scheduling.</summary>
    public AgentChatQueue ImmediateInputQueue => this.chatQueueManager.ImmediateInputQueue;

    /// <summary>Items awaiting user approval.</summary>
    public ObservableCollection<AgentChatPendingApprovalItem> PendingApprovalItems { get; } = [];

    /// <summary>Underlying queue manager, for advanced queue behaviors.</summary>
    public AgentInputQueueManager InputQueueManager => this.queueManager;

    public AgentChatQueueManager QueueManager => this.chatQueueManager;

    public bool IsBusy => this.isBusy;

    public string DisplayName { get; private set; } = string.Empty;

    public string AgentSessionId => this.agentSessionId;

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
        => this.runningItems.Create(items);

    public void UpdateRunningItem(AgentChatRunningItem item, AgentChatHistoryItem[] model)
        => this.runningItems.Update(item, model);

    public void CompleteRunningItem(
        AgentChatRunningItem item,
        bool writeToHistory = true)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (writeToHistory)
        {
            if (item.Items != null)
            {
                foreach (var historyItem in item.Items)
                {
                    this.History.Add(historyItem);
                    this.TurnCompleted?.Invoke(this, historyItem);
                }
            }
        }

        this.runningItems.Remove(item);
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
            this.History.Add(new AgentChatHistoryItem
            {
                Role = message.Role,
                Contents = message.Contents.ToArray(),
                IsInProgress = false,
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        this.runningItems.Idle -= this.OnRunningItemsIdle;
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
                    IsInProgress = true,
                }
            };
        }

        var chatHistoryItems = chatResponse.Messages.Reverse().Select((message, index) => new AgentChatHistoryItem
        {
            Role = message.Role,
            Contents = message.Contents.ToArray(),
            IsInProgress = index == 0
                && !lastIsToolResult
                && !IsTerminalAssistantUpdate(agentResponseUpdate)
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
                        queueStateSignal.Wait(cancellationToken);
                    }
                }

                var useHistoryPlaceholder = true;
                var historyPlaceholderIndex = -1;
                if (useHistoryPlaceholder)
                {
                    this.historyService.BeginInvocation(chatMessagesToSubmit.ToArray());
                    this.History.Add(new AgentChatHistoryItem
                    {
                        Role = ChatRole.Assistant,
                        IsInProgress = true,
                    });
                    historyPlaceholderIndex = this.History.Count - 1;
                }

                AgentChatRunningItem? currentPartialTextResponseItem = this.CreateRunningItem([
                    new AgentChatHistoryItem
                    {
                        Role = ChatRole.Assistant,
                        IsInProgress = true,
                    }]);
                var shouldWriteRunningItemToHistory = false;

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

                    shouldWriteRunningItemToHistory = false;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    var runningItem = currentPartialTextResponseItem
                        ?? throw new InvalidOperationException("Running item was unexpectedly null while handling a provider error.");
                    var existingItems = runningItem.Items ?? [];
                    var errorItems = existingItems
                        .Select(item => item with { IsInProgress = false })
                        .Concat([
                            new AgentChatHistoryItem
                            {
                                Role = ChatRole.Assistant,
                                Contents = [new ErrorContent($"Provider error: {ex.Message}")],
                                IsInProgress = false,
                            },
                        ])
                        .ToArray();

                    this.UpdateRunningItem(runningItem, errorItems);
                    shouldWriteRunningItemToHistory = false;
                }
                finally
                {
                    if (useHistoryPlaceholder && historyPlaceholderIndex >= 0)
                    {
                        this.CommitRunningItemToHistoryPlaceholder(currentPartialTextResponseItem, historyPlaceholderIndex);
                    }

                    this.CompleteRunningItem(currentPartialTextResponseItem, shouldWriteRunningItemToHistory);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            this.queueManager.QueueStateChanged -= OnQueueStateChanged;
            this.isBusy = false;
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
        return session
            .RunStreamAsync(messages, cancellationToken);
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

    private static bool IsTerminalAssistantUpdate(AgentResponseUpdate update)
    {
        if (update.Role != ChatRole.Assistant)
        {
            return false;
        }

        if (update.FinishReason is null)
        {
            return false;
        }

        return !IsToolContinuationFinishReason(update.FinishReason);
    }

    private void CommitRunningItemToHistoryPlaceholder(AgentChatRunningItem? runningItem, int placeholderIndex)
    {
        if (runningItem?.Items is null || runningItem.Items.Length == 0)
        {
            return;
        }

        if (placeholderIndex < 0 || placeholderIndex >= this.History.Count)
        {
            return;
        }

        var finalItem = runningItem.Items
            .LastOrDefault(static item =>
                item.Role == ChatRole.Assistant
                && (!string.IsNullOrWhiteSpace(item.Text) || !string.IsNullOrWhiteSpace(item.ReasoningText)))
            ?? runningItem.Items
            .LastOrDefault(static item => item.Role == ChatRole.Assistant)
            ?? runningItem.Items[^1];
        finalItem = finalItem with { IsInProgress = false };

        this.History[placeholderIndex] = finalItem;

        if (finalItem.Role == ChatRole.Assistant)
        {
            this.TurnCompleted?.Invoke(this, finalItem);
        }
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
        if (this.processingStarted)
        {
            return;
        }

        this.processingStarted = true;
        this.processTask = Task.Run(() => this.RunProcessLoopAsync(this.cts.Token));
    }

    private async Task InitializeMcpToolsAsync(CancellationToken cancellationToken = default)
    {
        var agent = this.agentDefinition;
        var client = this.client;
        var chatOptions = this.chatOptions;
        var services = this.request.AgentServices;
        var persistenceProvider = this.persistenceProvider;

        if (agent is null || client is null || chatOptions?.ChatOptions is null || persistenceProvider is null)
        {
            return;
        }

        var agentTools = AgentFactory.ExtractTools(agent);
        var hasMcpTools = agentTools?.OfType<McpTool>().Any() == true;
        if (!hasMcpTools)
        {
            return;
        }

        this.QueueManager.SetQueueHeld(this.DefaultInputQueue, held: true);
        var startupRunningItem = this.CreateRunningItem(new AgentChatHistoryItem
        {
            Role = AgentChatHistoryItem.DiagnosticChatRole,
            Contents = new AIContent[] { new TextContent("Initializing tools...") },
        });
        _ = Task.Run(
            async () =>
            {
                try
                {
                    var runtimeTools = await CreateRuntimeToolsAsync(
                        agentTools,
                        services,
                        text => this.UpdateRunningItem(startupRunningItem, [new AgentChatHistoryItem
                        {
                            Role = AgentChatHistoryItem.DiagnosticChatRole,
                            Contents = new AIContent[] { new TextContent(text) },
                        }]),
                        resource => this.RegisterOwnedResource(resource),
                        cancellationToken).ConfigureAwait(false);

                    chatOptions.ChatOptions.Tools = runtimeTools;
                    var rebuiltAgent = new ChatClientAgent(client, chatOptions);
                    this.chatClientAgent = rebuiltAgent;
                    var session = await rebuiltAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(this.request.AgentSessionId))
                    {
                        persistenceProvider.SetAgentSessionId(session, this.request.AgentSessionId);
                    }
                    var refreshedAgentSessionId = persistenceProvider.ExtractAgentSessionId(session);
                    await this.ReinitializeSessionAsync(
                        new AgentChatSession(rebuiltAgent, session),
                        refreshedAgentSessionId,
                        cancellationToken).ConfigureAwait(false);
                    this.UpdateRunningItem(startupRunningItem, [new AgentChatHistoryItem
                    {
                        Role = AgentChatHistoryItem.DiagnosticChatRole,
                        Contents = new AIContent[] { new TextContent(BuildStartupReadyMessage(runtimeTools)) },
                    }]);
                    this.CompleteRunningItem(startupRunningItem, true);
                }
                catch (Exception ex)
                {
                    this.UpdateRunningItem(startupRunningItem, [new AgentChatHistoryItem
                    {
                        Role = AgentChatHistoryItem.DiagnosticChatRole,
                        Contents = new AIContent[] { new ErrorContent($"Agent startup failed: {ex.Message}") },
                    }]);
                    this.CompleteRunningItem(startupRunningItem, true);
                }
                finally
                {
                    this.QueueManager.SetQueueHeld(this.DefaultInputQueue, held: false);
                }
            },
            cancellationToken);

        await Task.CompletedTask;
    }

    private static async Task<List<AITool>> CreateRuntimeToolsAsync(
        IList<Tool>? agentTools,
        AgentServices? services,
        Action<string>? progressCallback,
        Action<IAsyncDisposable>? resourceCallback,
        CancellationToken cancellationToken)
    {
        var resolvedTools = new List<AITool>();
        if (agentTools is null || agentTools.Count == 0)
        {
            return resolvedTools;
        }

        foreach (var tool in agentTools)
        {
            switch (tool)
            {
                case McpTool mcpTool:
                {
                    var toolServerName = string.IsNullOrWhiteSpace(mcpTool.ServerName) ? mcpTool.Name : mcpTool.ServerName;
                    progressCallback?.Invoke($"Opening MCP server '{toolServerName}'...");

                    var transport = CreateMcpTransport(mcpTool, services);
                    var client = await McpClient.CreateAsync(
                        transport,
                        null,
                        services?.LoggerFactory,
                        cancellationToken).ConfigureAwait(false);
                    resourceCallback?.Invoke(client);

                    var mcpTools = await client.ListToolsAsync(options: null, cancellationToken).ConfigureAwait(false);
                    var allowed = mcpTool.AllowedTools;
                    if (allowed is { Count: > 0 })
                    {
                        var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
                        mcpTools = [.. mcpTools.Where(t => allowedSet.Contains(t.Name))];
                    }

                    resolvedTools.AddRange(mcpTools);
                    progressCallback?.Invoke($"Opened MCP server '{toolServerName}' ({mcpTools.Count} tools).");
                    break;
                }
                case CustomTool { Kind: "web_search" }:
                    resolvedTools.Add(new WebSearchTool(logger: services?.LoggerFactory?.CreateLogger<WebSearchTool>()));
                    break;
                case CustomTool { Kind: "web_request" }:
                    resolvedTools.Add(new WebRequestTool(logger: services?.LoggerFactory?.CreateLogger<WebRequestTool>()));
                    break;
            }
        }

        return resolvedTools;
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
            AnonymousConnection anonymous => CreateHttpTransport(
                anonymous.Endpoint,
                apiKey: null,
                tool.ServerName,
                services?.LoggerFactory),
            ApiKeyConnection apiKey => CreateHttpTransport(
                apiKey.Endpoint,
                ResolveApiKey(apiKey.ApiKey, tool.ServerName),
                tool.ServerName,
                services?.LoggerFactory),
            null => throw new InvalidOperationException($"MCP tool '{tool.Name}' must define a connection."),
            _ => throw new InvalidOperationException(
                $"MCP tool '{tool.Name}' has unsupported connection type '{tool.Connection.GetType().Name}'."),
        };
    }

    private static IClientTransport CreateHttpTransport(
        string? endpoint,
        string? apiKey,
        string? serverName,
        ILoggerFactory? loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("MCP tool endpoint is required.");
        }

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint, UriKind.Absolute),
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

    private void OnRunningItemsIdle(object? sender, EventArgs e)
        => this.Idle?.Invoke(this, EventArgs.Empty);

}
