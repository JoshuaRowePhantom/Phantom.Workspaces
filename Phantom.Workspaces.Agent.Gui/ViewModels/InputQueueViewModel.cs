using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Agent.Gui.ViewModels.Collections;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

/// <summary>
/// ViewModel for the text input box and queue list.
/// Normal mode: Enter submits, Shift+Enter enters formatted mode.
/// Formatted mode: Enter = newline, Ctrl+Enter submits, Esc = exit without submit.
/// </summary>
public sealed class InputQueueViewModel : ViewModelBase
{
    private readonly IAgentChat agentChat;
    private readonly IAgentInputQueues inputQueues;
    private readonly Dictionary<string, InputQueueGroupViewModel> queueViewModels = new(StringComparer.Ordinal);
    private readonly List<string> queueUseHistory = [];
    // #1485: serialize RefreshQueues so concurrent Changed notifications delivered on
    // TaskScheduler.Default (the fallback foregroundScheduler when no dispatcher is captured,
    // e.g. in headless tests) cannot both observe a dictionary miss for the same queue and
    // insert two InputQueueGroupViewModel instances for the same QueueId into Queues.
    private readonly object queuesLock = new();
    private string? hiddenBuiltInQueueId;
    private bool hasMultipleQueues;
    private readonly ICommand holdAllQueuesCommand;
    private readonly ICommand unholdAllQueuesCommand;
    private readonly ICommand toggleHoldAllQueuesCommand;
    private readonly ICommand submitToMostRecentQueueCommand;
    private readonly ICommand submitToNewQueueCommand;
    private readonly ICommand createNewQueueCommand;
    private int ignoredQueueChangedEvents;

    public InputQueueViewModel(IAgentChat agentChat)
        : this(
            agentChat,
            agentChat.InputQueues.DefaultQueue.Snapshot.QueueId,
            agentChat.InputQueues.ImmediateQueue.Snapshot.QueueId)
    {
    }

    private InputQueueViewModel(IAgentChat agentChat, string defaultQueueId, string? hiddenBuiltInQueueId)
    {
        this.agentChat = agentChat ?? throw new ArgumentNullException(nameof(agentChat));
        this.inputQueues = agentChat.InputQueues;
        this.DefaultQueueId = defaultQueueId;
        this.hiddenBuiltInQueueId = hiddenBuiltInQueueId;
        this.DefaultComposer = new QueueComposerViewModel(this, this.DefaultQueueId, isDefaultComposer: true);
        this.SubmitToDefaultQueueCommand = this.DefaultComposer.SubmitCommand;
        this.holdAllQueuesCommand = new RelayCommand(this.HoldAllQueues);
        this.unholdAllQueuesCommand = new RelayCommand(this.UnholdAllQueues);
        this.toggleHoldAllQueuesCommand = new RelayCommand(this.ToggleHoldAllQueues);
        this.submitToMostRecentQueueCommand = new RelayCommand(() => this.SubmitToMostRecentQueue());
        this.submitToNewQueueCommand = new RelayCommand(() => this.SubmitToNewQueue());
        this.createNewQueueCommand = new RelayCommand(this.CreateNewQueue);
        this.inputQueues.Changed += this.OnQueuesChanged;
        this.RefreshQueues();
    }

    public InputQueueViewModel(
        AgentChat agentChat,
        AgentChatQueue defaultInputQueue,
        AgentInputQueueManager? inputQueueManager = null)
        : this(
            agentChat,
            defaultInputQueue.IsImmediate
                ? ((IAgentChat)agentChat).InputQueues.ImmediateQueue.Snapshot.QueueId
                : ((IAgentChat)agentChat).InputQueues.DefaultQueue.Snapshot.QueueId,
            defaultInputQueue.IsImmediate
                ? ((IAgentChat)agentChat).InputQueues.DefaultQueue.Snapshot.QueueId
                : ((IAgentChat)agentChat).InputQueues.ImmediateQueue.Snapshot.QueueId)
    {
    }

    internal string DefaultQueueId { get; private set; }

    public QueueComposerViewModel DefaultComposer { get; }

    public bool HasQueueManager => true;

    public bool HasMultipleQueues
    {
        get => this.hasMultipleQueues;
        private set => this.SetProperty(ref this.hasMultipleQueues, value);
    }

    public ObservableCollection<InputQueueGroupViewModel> Queues { get; } = [];

    public IReadOnlyList<AgentInputQueueSnapshot> InputQueues => this.GetVisibleQueues();

    public string InputText
    {
        get => this.DefaultComposer.InputText;
        set => this.DefaultComposer.InputText = value;
    }

    /// <summary>
    /// True when the input box is in multi-line formatted mode.
    /// In this mode Enter inserts a newline; Ctrl+Enter submits.
    /// </summary>
    public bool IsFormattedMode
    {
        get => this.DefaultComposer.IsFormattedMode;
        set => this.DefaultComposer.IsFormattedMode = value;
    }

    public ICommand SubmitToDefaultQueueCommand { get; }

    public ICommand HoldAllQueuesCommand => this.holdAllQueuesCommand;

    public ICommand UnholdAllQueuesCommand => this.unholdAllQueuesCommand;

    public ICommand ToggleHoldAllQueuesCommand => this.toggleHoldAllQueuesCommand;

    public ICommand SubmitToMostRecentQueueCommand => this.submitToMostRecentQueueCommand;

    public ICommand SubmitToNewQueueCommand => this.submitToNewQueueCommand;

    public ICommand CreateNewQueueCommand => this.createNewQueueCommand;

    /// <summary>
    /// Enters formatted mode. Called on Shift+Enter in normal mode.
    /// </summary>
    public void EnterFormattedMode()
    {
        this.DefaultComposer.EnterFormattedMode();
    }

    /// <summary>
    /// Exits formatted mode without submitting. Called on Esc in formatted mode.
    /// </summary>
    public void ExitFormattedMode()
    {
        this.DefaultComposer.ExitFormattedMode();
    }

    /// <summary>
    /// Submits current text to the default queue and returns to normal mode.
    /// </summary>
    public void SubmitToDefaultQueue()
    {
        this.DefaultComposer.Submit();
    }

    public bool SubmitToMostRecentQueue()
    {
        var queueId = this.queueUseHistory.FirstOrDefault(q => q != this.DefaultQueueId && this.TryGetQueueSnapshot(q, out _));
        if (queueId is not null)
        {
            return this.DefaultComposer.Submit(queueId);
        }

        if (this.TryGetQueueSnapshot(this.DefaultQueueId, out var defaultQueue) && defaultQueue.IsImmediate)
        {
            var newQueue = this.CreateQueue(
                this.InputQueues.All(static q => q.Immediacy == AgentInputQueueImmediacy.Held)
                    ? AgentInputQueueImmediacy.Held
                    : AgentInputQueueImmediacy.Queue);
            if (newQueue is not null)
            {
                this.RecordQueueUse(newQueue);
                return this.DefaultComposer.Submit(newQueue);
            }
        }

        return this.DefaultComposer.Submit(this.DefaultQueueId);
    }

    public bool SubmitToNewQueue()
    {
        if (string.IsNullOrWhiteSpace(this.InputText))
        {
            return false;
        }

        // Ctrl+Shift+Q always stages the new queue in the Held state so the user can configure,
        // reorder, or release it before any work is dispatched (issue #1070).
        var queueId = this.CreateQueue(AgentInputQueueImmediacy.Held);
        return queueId is not null && this.DefaultComposer.Submit(queueId);
    }

    public void CreateNewQueue()
    {
        var queueId = this.CreateQueue(
            this.InputQueues.All(static queue => queue.Immediacy == AgentInputQueueImmediacy.Held)
                ? AgentInputQueueImmediacy.Held
                : AgentInputQueueImmediacy.Queue);
        if (queueId is not null)
        {
            this.RecordQueueUse(queueId);
        }
    }

    public void ToggleHoldAllQueues()
    {
        if (this.InputQueues.Count == 0)
        {
            return;
        }

        var holdAll = this.InputQueues.Any(static queue => queue.Immediacy != AgentInputQueueImmediacy.Held);
        this.SetAllQueuesHeld(holdAll);
    }

    public void HoldAllQueues()
    {
        this.SetAllQueuesHeld(held: true);
    }

    public void UnholdAllQueues()
    {
        this.SetAllQueuesHeld(held: false);
    }

    public void SetQueueImmediacy(string queueId, AgentInputQueueImmediacy immediacy)
    {
        if (!this.TryGetQueueSnapshot(queueId, out var snapshot))
        {
            return;
        }

        this.Apply((commandId, expectedRevision) => this.inputQueues.Configure(new ConfigureAgentInputQueueRequest
        {
            QueueId = queueId,
            Configuration = new AgentInputQueueConfiguration
            {
                Name = snapshot.Name,
                Immediacy = immediacy,
                Priority = snapshot.Priority,
                CoalescingKey = snapshot.CoalescingKey,
            },
            CommandId = commandId,
            ExpectedRevision = expectedRevision,
        }));
        this.RefreshQueue(queueId);
    }

    public void Dispose()
    {
        this.inputQueues.Changed -= this.OnQueuesChanged;
        lock (this.queuesLock)
        {
            foreach (var viewModel in this.queueViewModels.Values)
            {
                viewModel.Dispose();
            }
        }
    }

    public void RemoveQueueItem(string queueId, int index)
    {
        if (!this.TryGetQueueSnapshot(queueId, out var snapshot)
            || index < 0
            || index >= snapshot.Items.Length)
        {
            return;
        }

        this.RemoveQueueItem(queueId, snapshot.Items[index].ItemId);
    }

    public void RemoveQueueItem(AgentChatQueue queue, int index)
        => this.RemoveQueueItem(this.ResolveQueueId(queue), index);

    public void RemoveQueueItem(string queueId, string itemId)
    {
        this.Apply((commandId, expectedRevision) => this.inputQueues.Remove(new RemoveAgentInputQueueItemRequest
        {
            QueueId = queueId,
            ItemId = itemId,
            CommandId = commandId,
            ExpectedRevision = expectedRevision,
        }));
        this.RefreshQueue(queueId);
    }

    public void RemoveQueueItem(AgentChatQueue queue, AgentInputItem item)
        => this.RemoveQueueItem(this.ResolveQueueId(queue), item.ItemId);

    public bool RemoveInputQueue(string queueId)
    {
        var result = this.Apply((commandId, expectedRevision) => this.inputQueues.DeleteQueue(new DeleteAgentInputQueueRequest
        {
            QueueId = queueId,
            CommandId = commandId,
            ExpectedRevision = expectedRevision,
        }));

        if (result.Status != AgentInputQueueCommandStatus.Applied)
        {
            return false;
        }

        this.queueUseHistory.Remove(queueId);
        return true;
    }

    public bool RemoveInputQueue(AgentChatQueue queue)
        => this.RemoveInputQueue(this.ResolveQueueId(queue));

    private void RecordQueueUse(string queueId)
    {
        this.queueUseHistory.Remove(queueId);
        this.queueUseHistory.Insert(0, queueId);
    }

    public void UpdateQueueItem(string queueId, int index, string text)
    {
        if (!this.TryGetQueueSnapshot(queueId, out var snapshot)
            || index < 0
            || index >= snapshot.Items.Length)
        {
            return;
        }

        this.UpdateQueueItem(queueId, snapshot.Items[index].ItemId, text);
    }

    public void UpdateQueueItem(AgentChatQueue queue, int index, string text)
        => this.UpdateQueueItem(this.ResolveQueueId(queue), index, text);

    public void UpdateQueueItem(string queueId, string itemId, string text)
    {
        if (!this.TryGetItemSnapshot(queueId, itemId, out var item))
        {
            return;
        }

        this.Apply((commandId, expectedRevision) => this.inputQueues.Edit(new EditAgentInputQueueItemRequest
        {
            QueueId = queueId,
            ItemId = itemId,
            Messages = UpdateMessages(item.Messages, text),
            CommandId = commandId,
            ExpectedRevision = expectedRevision,
        }));
        this.RefreshQueue(queueId);
    }

    public void UpdateQueueItem(AgentChatQueue queue, AgentInputItem item, string text)
        => this.UpdateQueueItem(this.ResolveQueueId(queue), item.ItemId, text);

    public void SendQueueItemImmediately(string queueId, string itemId, string text)
    {
        if (!this.TryGetItemSnapshot(queueId, itemId, out var item))
        {
            return;
        }

        var contents = UpdateMessages(item.Messages, text)[0].Contents.ToArray();
        this.RemoveQueueItem(queueId, itemId);
        this.AppendToQueue(this.DefaultQueueId, contents);
    }

    public void SendQueueItemImmediately(AgentChatQueue queue, AgentInputItem item, string text)
        => this.SendQueueItemImmediately(this.ResolveQueueId(queue), item.ItemId, text);

    public void RemoveQueueItemContent(string queueId, int index, int contentIndex)
    {
        if (!this.TryGetQueueSnapshot(queueId, out var snapshot)
            || index < 0
            || index >= snapshot.Items.Length)
        {
            return;
        }

        this.RemoveQueueItemContent(queueId, snapshot.Items[index].ItemId, contentIndex);
    }

    public void RemoveQueueItemContent(AgentChatQueue queue, int index, int contentIndex)
        => this.RemoveQueueItemContent(this.ResolveQueueId(queue), index, contentIndex);

    public void RemoveQueueItemContent(string queueId, string itemId, int contentIndex)
    {
        if (!this.TryGetItemSnapshot(queueId, itemId, out var item))
        {
            return;
        }

        var existingMessage = item.Messages.Length > 0
            ? item.Messages[0]
            : new ChatMessage(ChatRole.User, []);
        var contents = existingMessage.Contents.ToList();
        if (contentIndex < 0 || contentIndex >= contents.Count)
        {
            return;
        }

        contents.RemoveAt(contentIndex);
        if (contents.Count == 0)
        {
            this.RemoveQueueItem(queueId, itemId);
            return;
        }

        var updatedMessages = item.Messages.ToArray();
        updatedMessages[0] = new ChatMessage(ChatRole.User, contents);
        this.Apply((commandId, expectedRevision) => this.inputQueues.Edit(new EditAgentInputQueueItemRequest
        {
            QueueId = queueId,
            ItemId = itemId,
            Messages = updatedMessages,
            CommandId = commandId,
            ExpectedRevision = expectedRevision,
        }));
        this.RefreshQueue(queueId);
    }

    public void RemoveQueueItemContent(AgentChatQueue queue, AgentInputItem item, int contentIndex)
        => this.RemoveQueueItemContent(this.ResolveQueueId(queue), item.ItemId, contentIndex);

    public void AppendToQueue(string queueId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        this.AppendToQueue(queueId, [new TextContent(text)]);
    }

    public void AppendToQueue(AgentChatQueue queue, string text)
        => this.AppendToQueue(this.ResolveQueueId(queue), text);

    public void AppendToQueue(string queueId, IReadOnlyList<AIContent> contents)
    {
        if (contents.Count == 0)
        {
            return;
        }

        this.Apply((commandId, expectedRevision) => this.inputQueues.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = queueId,
            Messages = [new ChatMessage(ChatRole.User, contents.ToList())],
            CommandId = commandId,
            ExpectedRevision = expectedRevision,
        }));
        this.RecordQueueUse(queueId);
        this.RefreshQueue(queueId);
    }

    public void AppendToQueue(AgentChatQueue queue, IReadOnlyList<AIContent> contents)
        => this.AppendToQueue(this.ResolveQueueId(queue), contents);

    public void HideQueueComposer(string queueId)
    {
        InputQueueGroupViewModel? viewModel;
        lock (this.queuesLock)
        {
            this.queueViewModels.TryGetValue(queueId, out viewModel);
        }

        viewModel?.HideComposer();
    }

    public void HideQueueComposer(AgentChatQueue queue)
        => this.HideQueueComposer(this.ResolveQueueId(queue));

    internal bool TryGetQueueSnapshot(string queueId, out AgentInputQueueSnapshot snapshot)
    {
        foreach (var queue in this.GetVisibleQueues())
        {
            if (string.Equals(queue.QueueId, queueId, StringComparison.Ordinal))
            {
                snapshot = queue;
                return true;
            }
        }

        snapshot = default;
        return false;
    }

    internal bool TryGetItemSnapshot(string queueId, string itemId, out AgentInputItemSnapshot snapshot)
    {
        if (this.TryGetQueueSnapshot(queueId, out var queueSnapshot))
        {
            foreach (var item in queueSnapshot.Items)
            {
                if (string.Equals(item.ItemId, itemId, StringComparison.Ordinal))
                {
                    snapshot = item;
                    return true;
                }
            }
        }

        snapshot = default;
        return false;
    }

    private void SetAllQueuesHeld(bool held)
    {
        if (this.InputQueues.Count == 0)
        {
            return;
        }

        var snapshots = this.InputQueues.ToArray();
        foreach (var queue in snapshots)
        {
            var targetImmediacy = held
                ? AgentInputQueueImmediacy.Held
                : queue.IsImmediate
                    ? AgentInputQueueImmediacy.Immediate
                    : AgentInputQueueImmediacy.Queue;
            if (queue.Immediacy == targetImmediacy)
            {
                continue;
            }

            var currentQueue = queue;
            var currentTargetImmediacy = targetImmediacy;
            this.Apply((commandId, expectedRevision) => this.inputQueues.Configure(new ConfigureAgentInputQueueRequest
            {
                QueueId = currentQueue.QueueId,
                Configuration = new AgentInputQueueConfiguration
                {
                    Name = currentQueue.Name,
                    Immediacy = currentTargetImmediacy,
                    Priority = currentQueue.Priority,
                    CoalescingKey = currentQueue.CoalescingKey,
                },
                CommandId = commandId,
                ExpectedRevision = expectedRevision,
            }));
        }
    }

    private void OnQueuesChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref this.ignoredQueueChangedEvents, 0) > 0)
        {
            return;
        }

        this.RefreshQueues();
    }

    private void RefreshQueue(string queueId)
    {
        lock (this.queuesLock)
        {
            if (this.queueViewModels.TryGetValue(queueId, out var viewModel))
            {
                viewModel.Refresh();
            }
        }
    }

    private void RefreshQueues()
    {
        lock (this.queuesLock)
        {
            var snapshots = this.GetVisibleQueues();
            var queueIds = snapshots.Select(static q => q.QueueId).ToArray();
            foreach (var existing in this.Queues.ToArray())
            {
                if (existing is null || !queueIds.Contains(existing.QueueId, StringComparer.Ordinal))
                {
                    if (existing is not null)
                    {
                        this.Queues.Remove(existing);
                        this.queueViewModels.Remove(existing.QueueId);
                        existing.Dispose();
                    }
                }
            }

            for (var index = 0; index < snapshots.Length; index++)
            {
                var snapshot = snapshots[index];
                if (!this.queueViewModels.TryGetValue(snapshot.QueueId, out var existing))
                {
                    var composer = snapshot.IsDefault
                        ? this.DefaultComposer
                        : new QueueComposerViewModel(this, snapshot.QueueId, isDefaultComposer: false);
                    existing = new InputQueueGroupViewModel(this, snapshot.QueueId, composer);
                    this.queueViewModels[snapshot.QueueId] = existing;
                    this.Queues.Insert(index, existing);
                }
                else
                {
                    var currentIndex = this.Queues.IndexOf(existing);
                    if (currentIndex >= 0 && currentIndex != index)
                    {
                        this.Queues.Move(currentIndex, index);
                    }
                }

                existing.Refresh();
            }

            this.UpdateQueueCollectionState();
        }
    }

    private void UpdateQueueCollectionState()
    {
        this.HasMultipleQueues = this.Queues.Count > 1;
        foreach (var queueViewModel in this.queueViewModels.Values.ToArray())
        {
            queueViewModel.Refresh();
        }
    }

    private string? CreateQueue(AgentInputQueueImmediacy immediacy)
    {
        var name = this.CreateQueueName();
        var result = this.Apply((commandId, expectedRevision) => this.inputQueues.CreateQueue(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration
            {
                Name = name,
                Immediacy = immediacy,
                Priority = 0,
            },
            CommandId = commandId,
            ExpectedRevision = expectedRevision,
        }));
        return result.Status == AgentInputQueueCommandStatus.Applied ? result.QueueId : null;
    }

    private string CreateQueueName()
    {
        var used = this.InputQueues
            .Select(static queue => queue.Name)
            .ToHashSet(StringComparer.Ordinal);
        var next = 1;
        while (used.Contains($"Queue {next}"))
        {
            next++;
        }

        return $"Queue {next}";
    }

    private string ResolveQueueId(AgentChatQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (queue.IsDefault)
        {
            return this.DefaultQueueId;
        }

        if (queue.IsImmediate)
        {
            return this.inputQueues.ImmediateQueue.Snapshot.QueueId;
        }

        var match = this.InputQueues.FirstOrDefault(snapshot => string.Equals(snapshot.Name, queue.Name, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(match.QueueId))
        {
            return match.QueueId;
        }

        throw new InvalidOperationException($"Queue '{queue.Name}' is no longer available.");
    }

    private AgentInputQueueSnapshot[] GetVisibleQueues()
        => this.inputQueues.Snapshot.Queues
            .Where(queue => !string.Equals(queue.QueueId, this.hiddenBuiltInQueueId, StringComparison.Ordinal))
            .ToArray();

    private static ChatMessage[] UpdateMessages(ImmutableArray<ChatMessage> messages, string text)
    {
        var existingMessage = messages.Length > 0
            ? messages[0]
            : new ChatMessage(ChatRole.User, []);
        var contents = existingMessage.Contents.ToList();
        var textContentIndex = contents.FindIndex(static content => content is TextContent);
        if (textContentIndex >= 0)
        {
            contents[textContentIndex] = new TextContent(text);
        }
        else
        {
            contents.Insert(0, new TextContent(text));
        }

        var updatedMessages = messages.ToArray();
        if (updatedMessages.Length == 0)
        {
            updatedMessages = [new ChatMessage(ChatRole.User, contents)];
        }
        else
        {
            updatedMessages[0] = new ChatMessage(ChatRole.User, contents);
        }

        return updatedMessages;
    }

    // #1485: owner-side commands complete synchronously on the caller's thread. The
    // adapter's Execute() re-reads AggregateRevision under stateLock, which can advance
    // between the caller's Snapshot.Revision read and that lock (e.g. a legacy Configure
    // or a background dequeue on another queue). A single-shot apply would then get
    // Conflict and silently drop the user's edit. This helper reads a fresh revision
    // and mints a new command id on each retry so UI mutations converge on a consistent
    // Applied result instead of being lost.
    internal AgentInputQueueCommandResult Apply(Func<Guid, long, AgentInputQueueCommandResult> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        const int maxAttempts = 8;
        AgentInputQueueCommandResult result = default;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var expectedRevision = this.inputQueues.Snapshot.Revision;
            result = command(Guid.NewGuid(), expectedRevision);
            if (result.Status != AgentInputQueueCommandStatus.Conflict)
            {
                break;
            }
        }
        if (result.Status == AgentInputQueueCommandStatus.Applied)
        {
            Interlocked.Increment(ref this.ignoredQueueChangedEvents);
        }
        this.RefreshQueues();
        return result;
    }
}
