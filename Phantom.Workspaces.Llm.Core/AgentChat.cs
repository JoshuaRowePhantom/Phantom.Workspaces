using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

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
    private const string RunningPartAssistantReasoning = "assistant-reasoning";
    private const string RunningPartAssistantText = "assistant-text";

    private readonly object sessionLock = new();
    private AgentChatSession session;
    private readonly AgentInputQueueManager queueManager;
    private readonly AgentChatQueueManager chatQueueManager;
    private readonly AgentChatHistoryService historyService;
    private readonly AgentRunningItems runningItems;
    private readonly List<IAsyncDisposable> ownedResources;
    private readonly object ownedResourcesLock = new();
    private readonly CancellationTokenSource cts = new();
    private readonly Task processTask;

    private bool isBusy;
    private readonly object processingStateLock = new();
    private CancellationTokenSource? activeRunCancellation;

    internal AgentChat(
        AgentChatSession session,
        AgentInputQueueManager queueManager,
        AgentFrameworkChatHistoryProvider chatHistoryProvider,
        string displayName = "",
        IReadOnlyList<IAsyncDisposable>? ownedResources = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(queueManager);
        this.session = session;
        this.queueManager = queueManager;
        this.DisplayName = displayName;
        this.chatQueueManager = new AgentChatQueueManager(queueManager);
        this.historyService = new AgentChatHistoryService(this.History, chatHistoryProvider);
        this.historyService.BindSession(session);
        this.runningItems = new AgentRunningItems(this.RunningItems);
        this.runningItems.Idle += this.OnRunningItemsIdle;
        this.ownedResources = ownedResources?.ToList() ?? [];
        this.processTask = Task.Run(() => this.RunProcessLoopAsync(this.cts.Token));
    }

    /// <summary>
    /// Fired when the active streaming turn finishes.
    /// The argument is the completed <see cref="AgentChatHistoryItem"/> that was added to <see cref="History"/>.
    /// Fires on the background processing thread.
    /// </summary>
    public event EventHandler<AgentChatHistoryItem>? TurnCompleted;

    public event EventHandler? Idle;

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

    public string DisplayName { get; }

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
            return this.session;
        }
    }

    private void SetSession(AgentChatSession nextSession)
    {
        ArgumentNullException.ThrowIfNull(nextSession);
        lock (this.sessionLock)
        {
            this.session = nextSession;
        }

        this.historyService.BindSession(nextSession);
    }

    private void OnRunningItemsIdle(object? sender, EventArgs e)
        => this.Idle?.Invoke(this, EventArgs.Empty);

}
