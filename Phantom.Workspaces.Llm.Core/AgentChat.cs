using System.Collections.ObjectModel;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// A single chat history entry (user or completed assistant turn).
/// </summary>
public sealed class AgentChatHistoryItem
{
    public ChatRole Role { get; init; }

    /// <summary>Completed text content of this turn.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Structured content blocks for this turn.</summary>
    public IReadOnlyList<AIContent> Contents { get; init; } = [];

    /// <summary>Reasoning/thinking text associated with this turn.</summary>
    public string ReasoningText { get; init; } = string.Empty;

    /// <summary>True while this assistant item is still pending or streaming.</summary>
    public bool IsInProgress { get; init; }
}

/// <summary>
/// A currently-running (streaming) agent response item.
/// </summary>
public sealed class AgentChatRunningItem
{
    private readonly StringBuilder buffer = new();

    /// <summary>Accumulated text so far from the streaming response.</summary>
    public string CurrentText => this.buffer.ToString();

    internal void Append(string text) => this.buffer.Append(text);

    internal AgentChatHistoryItem ToHistoryItem() =>
        new()
        {
            Role = ChatRole.Assistant,
            Text = this.buffer.ToString(),
            Contents = [new TextContent(this.buffer.ToString())],
            ReasoningText = string.Empty,
        };
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
    internal AgentChatQueue(AgentInputQueue queue, string name, bool isDefault)
    {
        this.Queue = queue;
        this.Name = name;
        this.IsDefault = isDefault;
        this.Queue.Changed += this.OnQueueChanged;
    }

    internal AgentInputQueue Queue { get; }

    public string Name { get; }

    public bool IsDefault { get; }

    public bool IsHeld => this.Queue.Immediacy == AgentInputQueueImmediacy.Held;

    public AgentInputQueueImmediacy Immediacy => this.Queue.Immediacy;

    public IReadOnlyList<ChatMessage> Items => this.Queue.Items;

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
    private readonly AgentInputQueueManager queueManager;
    private readonly CancellationTokenSource cts = new();
    private readonly Task processTask;
    private int nextUserQueuePriority = 10;
    private int userQueueSequence = 1;

    private AgentChatRunningItem? activeItem;
    private readonly Queue<int> pendingAssistantHistoryIndexes = new();
    private int? activeAssistantHistoryIndex;

    public AgentChat(AgentInputQueueManager queueManager)
    {
        ArgumentNullException.ThrowIfNull(queueManager);
        this.queueManager = queueManager;
        this.DefaultInputQueue = new AgentChatQueue(queueManager.ImmediateQueue, "Default Queue", isDefault: true);
        this.InputQueues.Add(this.DefaultInputQueue);
        this.queueManager.QueuePublished += this.OnQueuePublished;
        this.processTask = Task.Run(() => this.RunProcessLoopAsync(this.cts.Token));
    }

    /// <summary>
    /// Fired when a text chunk arrives from a streaming response.
    /// The argument is the new chunk (not the accumulated total).
    /// Fires on the background processing thread.
    /// </summary>
    public event EventHandler<string>? TextChunkReceived;

    /// <summary>
    /// Fired when the active streaming turn finishes.
    /// The argument is the completed <see cref="AgentChatHistoryItem"/> that was added to <see cref="History"/>.
    /// Fires on the background processing thread.
    /// </summary>
    public event EventHandler<AgentChatHistoryItem>? TurnCompleted;

    /// <summary>Completed conversation turns, in order.</summary>
    public ObservableCollection<AgentChatHistoryItem> History { get; } = [];

    /// <summary>Currently executing agent response items.</summary>
    public ObservableCollection<AgentChatRunningItem> RunningItems { get; } = [];

    /// <summary>All known input queues, including the default queue.</summary>
    public ObservableCollection<AgentChatQueue> InputQueues { get; } = [];

    /// <summary>The default input queue.</summary>
    public AgentChatQueue DefaultInputQueue { get; }

    /// <summary>Items awaiting user approval.</summary>
    public ObservableCollection<AgentChatPendingApprovalItem> PendingApprovalItems { get; } = [];

    /// <summary>Underlying queue manager, for advanced queue behaviors.</summary>
    public AgentInputQueueManager InputQueueManager => this.queueManager;

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

        var message = new ChatMessage(ChatRole.User, contents.ToList());
        this.queueManager.Enqueue(targetQueue.Queue, [message]);
    }

    public AgentChatQueue CreateInputQueue(
        string? name = null,
        AgentInputQueueImmediacy immediacy = AgentInputQueueImmediacy.Queue)
    {
        var queue = new AgentInputQueue(
            new AgentInputQueue.Parameters
            {
                Priority = this.nextUserQueuePriority++,
                Immediacy = immediacy,
            });
        var queueName = string.IsNullOrWhiteSpace(name)
            ? $"Queue {this.userQueueSequence++}"
            : name;
        var wrapped = new AgentChatQueue(queue, queueName, isDefault: false);
        this.queueManager.RegisterInputQueue(queue);
        this.InputQueues.Add(wrapped);
        return wrapped;
    }

    public bool RemoveInputQueue(AgentChatQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (queue.IsDefault)
        {
            return false;
        }

        var removedFromManager = this.queueManager.UnregisterInputQueue(queue.Queue);
        if (removedFromManager)
        {
            this.InputQueues.Remove(queue);
        }

        return removedFromManager;
    }

    public void SetQueueHeld(AgentChatQueue queue, bool held)
    {
        this.SetQueueImmediacy(queue, held
            ? AgentInputQueueImmediacy.Held
            : queue.IsDefault ? AgentInputQueueImmediacy.Immediate : AgentInputQueueImmediacy.Queue);
    }

    public void SetQueueImmediacy(AgentChatQueue queue, AgentInputQueueImmediacy immediacy)
    {
        ArgumentNullException.ThrowIfNull(queue);
        queue.Queue.Configure(new AgentInputQueue.Parameters
        {
            Priority = queue.Queue.Priority,
            Immediacy = immediacy,
            CoalescingKey = queue.Queue.CoalescingKey,
        });
        if (immediacy != AgentInputQueueImmediacy.Held)
        {
            this.queueManager.ServiceQueues(false);
        }
    }

    public void ClearQueue(AgentChatQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        while (true)
        {
            var expected = queue.Queue.Items;
            if (expected.Count == 0)
            {
                return;
            }

            if (queue.Queue.Clear(ref expected))
            {
                return;
            }
        }
    }

    public bool RemoveQueueItem(AgentChatQueue queue, int index)
    {
        ArgumentNullException.ThrowIfNull(queue);
        while (true)
        {
            var expected = queue.Queue.Items;
            if (index < 0 || index >= expected.Count)
            {
                return false;
            }

            if (queue.Queue.TryRemoveAt(ref expected, index))
            {
                return true;
            }
        }
    }

    public bool UpdateQueueItem(AgentChatQueue queue, int index, string text)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        while (true)
        {
            var expected = queue.Queue.Items;
            if (index < 0 || index >= expected.Count)
            {
                return false;
            }

            var existingMessage = expected[index];
            var contents = existingMessage.Contents.ToList();
            var textContentIndex = contents.FindIndex(static content => content is TextContent);
            if (string.IsNullOrWhiteSpace(text))
            {
                if (textContentIndex >= 0)
                {
                    contents.RemoveAt(textContentIndex);
                }
            }
            else if (textContentIndex >= 0)
            {
                contents[textContentIndex] = new TextContent(text);
            }
            else
            {
                contents.Insert(0, new TextContent(text));
            }

            if (contents.Count == 0)
            {
                if (queue.Queue.TryRemoveAt(ref expected, index))
                {
                    return true;
                }

                continue;
            }

            if (queue.Queue.TryUpdateAt(ref expected, index, new ChatMessage(ChatRole.User, contents)))
            {
                return true;
            }
        }
    }

    public bool RemoveQueueItemContent(AgentChatQueue queue, int index, int contentIndex)
    {
        ArgumentNullException.ThrowIfNull(queue);

        while (true)
        {
            var expected = queue.Queue.Items;
            if (index < 0 || index >= expected.Count)
            {
                return false;
            }

            var existingMessage = expected[index];
            var contents = existingMessage.Contents.ToList();
            if (contentIndex < 0 || contentIndex >= contents.Count)
            {
                return false;
            }

            contents.RemoveAt(contentIndex);
            if (contents.Count == 0)
            {
                if (queue.Queue.TryRemoveAt(ref expected, index))
                {
                    return true;
                }

                continue;
            }

            if (queue.Queue.TryUpdateAt(ref expected, index, new ChatMessage(ChatRole.User, contents)))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Requests an interrupt of the current streaming response.
    /// </summary>
    public void RequestInterrupt() => this.queueManager.RequestInterrupt();

    public async ValueTask DisposeAsync()
    {
        this.queueManager.QueuePublished -= this.OnQueuePublished;
        await this.cts.CancelAsync();
        try
        {
            await this.processTask;
        }
        catch (OperationCanceledException)
        {
        }

        this.cts.Dispose();
        this.queueManager.Complete();
    }

    private async Task RunProcessLoopAsync(CancellationToken cancellationToken)
    {
        await foreach (var update in this.queueManager.Process(cancellationToken).WithCancellation(cancellationToken))
        {
            this.HandleUpdate(update);
        }
    }

    private void HandleUpdate(AgentResponseUpdate update)
    {
        EnsureActiveAssistantHistoryIndex();

        if (!string.IsNullOrEmpty(update.Text))
        {
            if (this.activeItem is null)
            {
                this.activeItem = new AgentChatRunningItem();
                this.RunningItems.Add(this.activeItem);
            }

            this.activeItem.Append(update.Text);

            var idx = this.RunningItems.IndexOf(this.activeItem);
            if (idx >= 0)
            {
                this.RunningItems[idx] = this.activeItem;
            }

            this.TextChunkReceived?.Invoke(this, update.Text);

            if (this.activeAssistantHistoryIndex is int assistantIndex)
            {
                var assistantItem = this.History[assistantIndex];
                var updatedText = assistantItem.Text + update.Text;
                this.History[assistantIndex] = new AgentChatHistoryItem
                {
                    Role = assistantItem.Role,
                    Text = updatedText,
                    Contents = [new TextContent(updatedText)],
                    ReasoningText = assistantItem.ReasoningText,
                    IsInProgress = true,
                };
            }
        }

        var reasoningChunk = string.Concat(
            update.Contents
                .OfType<TextReasoningContent>()
                .Select(static content => content.Text));
        if (!string.IsNullOrEmpty(reasoningChunk) && this.activeAssistantHistoryIndex is int reasoningIndex)
        {
            var assistantItem = this.History[reasoningIndex];
            var updatedReasoning = assistantItem.ReasoningText + reasoningChunk;
            this.History[reasoningIndex] = new AgentChatHistoryItem
            {
                Role = assistantItem.Role,
                Text = assistantItem.Text,
                Contents = assistantItem.Contents,
                ReasoningText = updatedReasoning,
                IsInProgress = true,
            };
        }

        if (update.FinishReason is not null)
        {
            var completed = this.activeItem;
            this.activeItem = null;

            if (completed is not null)
            {
                this.RunningItems.Remove(completed);
            }

            if (this.activeAssistantHistoryIndex is int assistantIndex)
            {
                var assistantItem = this.History[assistantIndex];
                var completedHistoryItem = new AgentChatHistoryItem
                {
                    Role = assistantItem.Role,
                    Text = assistantItem.Text,
                    Contents = assistantItem.Contents,
                    ReasoningText = assistantItem.ReasoningText,
                    IsInProgress = false,
                };
                this.History[assistantIndex] = completedHistoryItem;
                this.TurnCompleted?.Invoke(this, completedHistoryItem);
                this.activeAssistantHistoryIndex = null;
            }
        }
    }

    private void OnQueuePublished(object? sender, AgentInputQueueManager.QueuePublishedEventArgs e)
    {
        if (e.Messages.Count == 0)
        {
            return;
        }

        foreach (var message in e.Messages)
        {
            if (message.Role != ChatRole.User)
            {
                continue;
            }

            var contents = message.Contents.ToArray();
            this.History.Add(new AgentChatHistoryItem
            {
                Role = ChatRole.User,
                Text = FormatContentAsText(contents),
                Contents = contents,
            });
        }

        var assistantIndex = this.History.Count;
        this.History.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Text = string.Empty,
            Contents = [new TextContent(string.Empty)],
            ReasoningText = string.Empty,
            IsInProgress = true,
        });
        this.pendingAssistantHistoryIndexes.Enqueue(assistantIndex);
    }

    private void EnsureActiveAssistantHistoryIndex()
    {
        if (this.activeAssistantHistoryIndex is not null)
        {
            return;
        }

        if (this.pendingAssistantHistoryIndexes.Count > 0)
        {
            this.activeAssistantHistoryIndex = this.pendingAssistantHistoryIndexes.Dequeue();
            return;
        }

        // Fallback: keep model resilient when updates arrive without a pre-created placeholder.
        var assistantIndex = this.History.Count;
        this.History.Add(new AgentChatHistoryItem
        {
            Role = ChatRole.Assistant,
            Text = string.Empty,
            Contents = [new TextContent(string.Empty)],
            ReasoningText = string.Empty,
            IsInProgress = true,
        });
        this.activeAssistantHistoryIndex = assistantIndex;
    }

    private static string FormatContentAsText(IReadOnlyList<AIContent> contents)
    {
        var builder = new StringBuilder();
        foreach (var content in contents)
        {
            switch (content)
            {
                case TextContent textContent:
                    builder.Append(textContent.Text);
                    break;
                case DataContent dataContent when !string.IsNullOrWhiteSpace(dataContent.MediaType):
                    builder.Append($"[{dataContent.MediaType}]");
                    break;
                case DataContent:
                    builder.Append("[data]");
                    break;
                case UriContent uriContent when !string.IsNullOrWhiteSpace(uriContent.MediaType):
                    builder.Append($"[{uriContent.MediaType}] {uriContent.Uri}");
                    break;
                case UriContent uriContent:
                    builder.Append(uriContent.Uri.ToString());
                    break;
                default:
                    builder.Append($"[{content.GetType().Name}]");
                    break;
            }
        }

        return builder.ToString();
    }
}
