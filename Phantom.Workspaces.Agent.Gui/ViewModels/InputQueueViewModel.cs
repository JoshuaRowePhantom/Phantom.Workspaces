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
    private readonly TaskScheduler foregroundScheduler;
    private AgentInputQueuesSnapshot? acknowledgedSnapshot;
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
    private bool disposed;

    public InputQueueViewModel(InputQueueViewModelOptions options)
        : this(
            (options ?? throw new ArgumentNullException(nameof(options))).AgentChat,
            options.DefaultQueueId ?? options.AgentChat.InputQueues.DefaultQueue.Snapshot.QueueId,
            options.HiddenBuiltInQueueId ?? options.AgentChat.InputQueues.ImmediateQueue.Snapshot.QueueId,
            options.ForegroundScheduler ?? TaskScheduler.Current)
    {
    }

    private InputQueueViewModel(
        IAgentChat agentChat,
        string defaultQueueId,
        string? hiddenBuiltInQueueId,
        TaskScheduler foregroundScheduler)
    {
        this.agentChat = agentChat ?? throw new ArgumentNullException(nameof(agentChat));
        this.inputQueues = agentChat.InputQueues;
        this.foregroundScheduler = foregroundScheduler ?? throw new ArgumentNullException(nameof(foregroundScheduler));
        this.DefaultQueueId = defaultQueueId;
        this.hiddenBuiltInQueueId = hiddenBuiltInQueueId;
        this.DefaultComposer = new QueueComposerViewModel(this, this.DefaultQueueId, isDefaultComposer: true);
        this.SubmitToDefaultQueueCommand = this.DefaultComposer.SubmitCommand;
        this.holdAllQueuesCommand = new AsyncRelayCommand(
            _ => this.ExecuteQueueOperationWithFeedbackAsync(() => this.HoldAllQueuesAsync()));
        this.unholdAllQueuesCommand = new AsyncRelayCommand(
            _ => this.ExecuteQueueOperationWithFeedbackAsync(() => this.UnholdAllQueuesAsync()));
        this.toggleHoldAllQueuesCommand = new AsyncRelayCommand(
            _ => this.ExecuteQueueOperationWithFeedbackAsync(() => this.ToggleHoldAllQueuesAsync()));
        this.submitToMostRecentQueueCommand = new AsyncRelayCommand(
            _ => this.DefaultComposer.SubmitWithFeedbackAsync(() => this.SubmitToMostRecentQueueAsync()));
        this.submitToNewQueueCommand = new AsyncRelayCommand(
            _ => this.DefaultComposer.SubmitWithFeedbackAsync(() => this.SubmitToNewQueueAsync()));
        this.createNewQueueCommand = new AsyncRelayCommand(
            _ => this.ExecuteQueueOperationWithFeedbackAsync(() => this.CreateNewQueueAsync()));
        this.inputQueues.Changed += this.OnQueuesChanged;
        this.RefreshQueues();
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

    internal void ReportSubmissionFailure() =>
        this.agentChat.EnqueueTransientDiagnostic(
            "The message could not be submitted. Your draft was preserved.");

    internal void ReportQueueOperationFailure() =>
        this.agentChat.EnqueueTransientDiagnostic(
            "The queue change could not be applied. Try again.");

    internal async Task ExecuteQueueOperationWithFeedbackAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedQueueOperationFailure(exception))
        {
            this.ReportQueueOperationFailure();
        }
    }

    internal async Task ExecuteQueueCommandWithFeedbackAsync(
        Func<Task<AgentInputQueueCommandResult>> operation)
    {
        try
        {
            var result = await operation().ConfigureAwait(false);
            if (result.Status != AgentInputQueueCommandStatus.Applied)
                this.ReportQueueOperationFailure();
        }
        catch (Exception exception) when (IsExpectedQueueOperationFailure(exception))
        {
            this.ReportQueueOperationFailure();
        }
    }

    internal async Task ExecuteQueueBooleanOperationWithFeedbackAsync(Func<Task<bool>> operation)
    {
        try
        {
            if (!await operation().ConfigureAwait(false))
                this.ReportQueueOperationFailure();
        }
        catch (Exception exception) when (IsExpectedQueueOperationFailure(exception))
        {
            this.ReportQueueOperationFailure();
        }
    }

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

    public Task SubmitToDefaultQueueAsync(CancellationToken ct = default) =>
        this.DefaultComposer.SubmitAsync(ct);

    public bool SubmitToMostRecentQueue()
    {
        var hasContent = !string.IsNullOrWhiteSpace(this.InputText)
            || this.DefaultComposer.HasAttachments;
        _ = this.DefaultComposer.SubmitWithFeedbackAsync(() => this.SubmitToMostRecentQueueAsync());
        return hasContent;
    }

    public async Task<bool> SubmitToMostRecentQueueAsync(CancellationToken ct = default)
    {
        var queueId = this.queueUseHistory.FirstOrDefault(q => q != this.DefaultQueueId && this.TryGetQueueSnapshot(q, out _));
        if (queueId is not null)
        {
            return await this.SubmitDefaultComposerOnForegroundAsync(queueId, ct).ConfigureAwait(false);
        }

        if (this.TryGetQueueSnapshot(this.DefaultQueueId, out var defaultQueue) && defaultQueue.IsImmediate)
        {
            var newQueue = await this.CreateQueueAsync(
                this.InputQueues.All(static q => q.Immediacy == AgentInputQueueImmediacy.Held)
                    ? AgentInputQueueImmediacy.Held : AgentInputQueueImmediacy.Queue,
                ct).ConfigureAwait(false);
            if (newQueue is not null)
            {
                return await this.RunOnForegroundAsync(
                    async () =>
                    {
                        this.RecordQueueUse(newQueue);
                        return await this.DefaultComposer.SubmitAsync(newQueue, ct);
                    },
                    ct).ConfigureAwait(false);
            }
        }

        return await this.SubmitDefaultComposerOnForegroundAsync(this.DefaultQueueId, ct).ConfigureAwait(false);
    }

    public bool SubmitToNewQueue()
    {
        var hasContent = !string.IsNullOrWhiteSpace(this.InputText)
            || this.DefaultComposer.HasAttachments;
        _ = this.DefaultComposer.SubmitWithFeedbackAsync(() => this.SubmitToNewQueueAsync());
        return hasContent;
    }

    public async Task<bool> SubmitToNewQueueAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(this.InputText))
        {
            return false;
        }

        // Ctrl+Shift+Q always stages the new queue in the Held state so the user can configure,
        // reorder, or release it before any work is dispatched (issue #1070).
        var queueId = await this.CreateQueueAsync(AgentInputQueueImmediacy.Held, ct).ConfigureAwait(false);
        return queueId is not null
            && await this.SubmitDefaultComposerOnForegroundAsync(queueId, ct).ConfigureAwait(false);
    }

    public void CreateNewQueue()
    {
        _ = this.ExecuteQueueOperationWithFeedbackAsync(() => this.CreateNewQueueAsync());
    }

    public async Task CreateNewQueueAsync(CancellationToken ct = default)
    {
        var queueId = await this.CreateQueueAsync(
            this.InputQueues.All(static queue => queue.Immediacy == AgentInputQueueImmediacy.Held)
                ? AgentInputQueueImmediacy.Held : AgentInputQueueImmediacy.Queue,
            ct).ConfigureAwait(false);
        if (queueId is not null)
        {
            await this.RunOnForegroundAsync(
                () =>
                {
                    this.RecordQueueUse(queueId);
                    return Task.CompletedTask;
                },
                ct).ConfigureAwait(false);
        }
    }

    public void ToggleHoldAllQueues()
    {
        _ = this.ExecuteQueueOperationWithFeedbackAsync(() => this.ToggleHoldAllQueuesAsync());
    }

    public Task ToggleHoldAllQueuesAsync(CancellationToken ct = default)
    {
        if (this.InputQueues.Count == 0)
        {
            return Task.CompletedTask;
        }

        var holdAll = this.InputQueues.Any(static queue => queue.Immediacy != AgentInputQueueImmediacy.Held);
        return this.SetAllQueuesHeldAsync(holdAll, ct);
    }

    public void HoldAllQueues()
    {
        _ = this.ExecuteQueueOperationWithFeedbackAsync(() => this.HoldAllQueuesAsync());
    }

    public void UnholdAllQueues()
    {
        _ = this.ExecuteQueueOperationWithFeedbackAsync(() => this.UnholdAllQueuesAsync());
    }

    public Task HoldAllQueuesAsync(CancellationToken ct = default) => this.SetAllQueuesHeldAsync(held: true, ct: ct);

    public Task UnholdAllQueuesAsync(CancellationToken ct = default) => this.SetAllQueuesHeldAsync(held: false, ct: ct);

    public void SetQueueImmediacy(string queueId, AgentInputQueueImmediacy immediacy)
    {
        _ = this.ExecuteQueueOperationWithFeedbackAsync(
            () => this.SetQueueImmediacyAsync(queueId, immediacy));
    }

    public async Task SetQueueImmediacyAsync(
        string queueId,
        AgentInputQueueImmediacy immediacy,
        CancellationToken ct = default)
    {
        if (!this.TryGetQueueSnapshot(queueId, out var snapshot))
        {
            return;
        }

        var result = await this.ApplyAsync((commandId, expectedRevision) => this.inputQueues.ConfigureAsync(new ConfigureAgentInputQueueRequest
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
        }, ct), ct).ConfigureAwait(false);
        if (result.Status != AgentInputQueueCommandStatus.Applied)
            this.ReportQueueOperationFailure();
    }

    public void Dispose()
    {
        this.disposed = true;
        this.inputQueues.Changed -= this.OnQueuesChanged;
        lock (this.queuesLock)
        {
            foreach (var viewModel in this.queueViewModels.Values)
            {
                viewModel.Dispose();
            }
        }
    }

    public void RemoveQueueItem(string queueId, string itemId)
    {
        _ = this.ExecuteQueueCommandWithFeedbackAsync(
            () => this.RemoveQueueItemAsync(queueId, itemId));
    }

    public Task<AgentInputQueueCommandResult> RemoveQueueItemAsync(
        string queueId,
        string itemId,
        CancellationToken ct = default)
        => this.ApplyAsync((commandId, expectedRevision) => this.inputQueues.RemoveAsync(new RemoveAgentInputQueueItemRequest
        {
            QueueId = queueId,
            ItemId = itemId,
            CommandId = commandId,
            ExpectedRevision = expectedRevision,
        }, ct), ct);

    public bool RemoveInputQueue(string queueId)
    {
        _ = this.ExecuteQueueBooleanOperationWithFeedbackAsync(
            () => this.RemoveInputQueueAsync(queueId));
        return true;
    }

    public async Task<bool> RemoveInputQueueAsync(string queueId, CancellationToken ct = default)
    {
        var result = await this.ApplyAsync((commandId, expectedRevision) => this.inputQueues.DeleteQueueAsync(new DeleteAgentInputQueueRequest
        {
            QueueId = queueId,
            CommandId = commandId,
            ExpectedRevision = expectedRevision,
        }, ct), ct).ConfigureAwait(false);

        if (result.Status != AgentInputQueueCommandStatus.Applied)
        {
            return false;
        }

        this.queueUseHistory.Remove(queueId);
        return true;
    }

    private void RecordQueueUse(string queueId)
    {
        this.queueUseHistory.Remove(queueId);
        this.queueUseHistory.Insert(0, queueId);
    }

    public void UpdateQueueItem(string queueId, string itemId, string text)
    {
        _ = this.ExecuteQueueCommandWithFeedbackAsync(
            () => this.UpdateQueueItemAsync(queueId, itemId, text));
    }

    public Task<AgentInputQueueCommandResult> UpdateQueueItemAsync(
        string queueId,
        string itemId,
        string text,
        CancellationToken ct = default)
    {
        if (!this.TryGetItemSnapshot(queueId, itemId, out var item))
        {
            return Task.FromResult(RejectedResult());
        }

        return this.ApplyAsync((commandId, expectedRevision) => this.inputQueues.EditAsync(new EditAgentInputQueueItemRequest
        {
            QueueId = queueId,
            ItemId = itemId,
            Messages = UpdateMessages(item.Messages, text),
            CommandId = commandId,
            ExpectedRevision = expectedRevision,
        }, ct), ct);
    }

    public void SendQueueItemImmediately(string queueId, string itemId, string text)
    {
        _ = this.ExecuteQueueOperationWithFeedbackAsync(
            () => this.SendQueueItemImmediatelyAsync(queueId, itemId, text));
    }

    public async Task SendQueueItemImmediatelyAsync(
        string queueId,
        string itemId,
        string text,
        CancellationToken ct = default)
    {
        var edited = await this.UpdateQueueItemAsync(queueId, itemId, text, ct).ConfigureAwait(false);
        if (edited.Status != AgentInputQueueCommandStatus.Applied
            || string.Equals(queueId, this.DefaultQueueId, StringComparison.Ordinal))
        {
            if (edited.Status != AgentInputQueueCommandStatus.Applied)
                this.ReportQueueOperationFailure();
            return;
        }

        var moved = await this.ApplyAsync((commandId, expectedRevision) => this.inputQueues.MoveAsync(
            new MoveAgentInputQueueItemRequest
            {
                SourceQueueId = queueId,
                ItemId = itemId,
                TargetQueueId = this.DefaultQueueId,
                CommandId = commandId,
                ExpectedRevision = expectedRevision,
            },
            ct), ct).ConfigureAwait(false);
        if (moved.Status != AgentInputQueueCommandStatus.Applied)
            this.ReportQueueOperationFailure();
    }

    internal void RemoveQueueItemContent(RemoveQueueItemContentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var queueId = request.QueueId;
        var itemId = request.ItemId;
        var contentIndex = request.ContentIndex;
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
            _ = this.ExecuteQueueCommandWithFeedbackAsync(
                () => this.RemoveQueueItemAsync(queueId, itemId));
            return;
        }

        var updatedMessages = item.Messages.ToArray();
        updatedMessages[0] = new ChatMessage(ChatRole.User, contents);
        _ = this.ExecuteQueueCommandWithFeedbackAsync(
            () => this.ApplyAsync((commandId, expectedRevision) => this.inputQueues.EditAsync(new EditAgentInputQueueItemRequest
            {
                QueueId = queueId,
                ItemId = itemId,
                Messages = updatedMessages,
                CommandId = commandId,
                ExpectedRevision = expectedRevision,
            }, CancellationToken.None), CancellationToken.None));
    }

    public void AppendToQueue(string queueId, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _ = this.ExecuteQueueCommandWithFeedbackAsync(
            () => this.AppendToQueueAsync(queueId, [new TextContent(text)]));
    }

    public void AppendToQueue(string queueId, IReadOnlyList<AIContent> contents)
    {
        _ = this.ExecuteQueueCommandWithFeedbackAsync(
            () => this.AppendToQueueAsync(queueId, contents));
    }

    public async Task<AgentInputQueueCommandResult> AppendToQueueAsync(
        string queueId,
        IReadOnlyList<AIContent> contents,
        CancellationToken ct = default)
    {
        if (contents.Count == 0)
        {
            return RejectedResult();
        }

        var result = await this.ApplyAsync((commandId, expectedRevision) => this.inputQueues.EnqueueAsync(new EnqueueAgentInputRequest
        {
            TargetQueueId = queueId,
            Messages = [new ChatMessage(ChatRole.User, contents.ToList())],
            CommandId = commandId,
            ExpectedRevision = expectedRevision,
        }, ct), ct).ConfigureAwait(false);
        if (result.Status == AgentInputQueueCommandStatus.Applied)
        {
            this.RecordQueueUse(queueId);
        }
        return result;
    }

    public void HideQueueComposer(string queueId)
    {
        InputQueueGroupViewModel? viewModel;
        lock (this.queuesLock)
        {
            this.queueViewModels.TryGetValue(queueId, out viewModel);
        }

        viewModel?.HideComposer();
    }

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

    private async Task SetAllQueuesHeldAsync(bool held, CancellationToken ct)
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
            await this.ApplyAsync((commandId, expectedRevision) => this.inputQueues.ConfigureAsync(new ConfigureAgentInputQueueRequest
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
            }, ct), ct).ConfigureAwait(false);
        }
    }

    private void OnQueuesChanged(object? sender, EventArgs e)
    {
        if (this.disposed)
        {
            return;
        }

        _ = this.RefreshOnForegroundAsync(() => this.acknowledgedSnapshot = null);
    }

    private Task RefreshOnForegroundAsync(Action? beforeRefresh = null)
    {
        if (this.disposed)
        {
            return Task.CompletedTask;
        }

        if (TaskScheduler.Current == this.foregroundScheduler)
        {
            beforeRefresh?.Invoke();
            this.RefreshQueues();
            return Task.CompletedTask;
        }

        return Task.Factory.StartNew(
            () =>
            {
                beforeRefresh?.Invoke();
                this.RefreshQueues();
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            this.foregroundScheduler);
    }

    private Task<bool> SubmitDefaultComposerOnForegroundAsync(
        string targetQueueId,
        CancellationToken ct) =>
        this.RunOnForegroundAsync(
            () => this.DefaultComposer.SubmitAsync(targetQueueId, ct),
            ct);

    private Task<T> RunOnForegroundAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (TaskScheduler.Current == this.foregroundScheduler)
        {
            return action();
        }

        return Task.Factory.StartNew(
            action,
            ct,
            TaskCreationOptions.DenyChildAttach,
            this.foregroundScheduler).Unwrap();
    }

    private Task RunOnForegroundAsync(Func<Task> action, CancellationToken ct)
    {
        if (TaskScheduler.Current == this.foregroundScheduler)
        {
            return action();
        }

        return Task.Factory.StartNew(
            action,
            ct,
            TaskCreationOptions.DenyChildAttach,
            this.foregroundScheduler).Unwrap();
    }

    private void RefreshQueue(string queueId)
    {
        if (this.disposed)
        {
            return;
        }

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

    private async Task<string?> CreateQueueAsync(
        AgentInputQueueImmediacy immediacy,
        CancellationToken ct)
    {
        var name = this.CreateQueueName();
        var result = await this.ApplyAsync((commandId, expectedRevision) => this.inputQueues.CreateQueueAsync(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration
            {
                Name = name,
                Immediacy = immediacy,
                Priority = 0,
            },
            CommandId = commandId,
            ExpectedRevision = expectedRevision,
        }, ct), ct).ConfigureAwait(false);
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

    private AgentInputQueueSnapshot[] GetVisibleQueues()
        => this.CurrentSnapshot.Queues
            .Where(queue => !string.Equals(queue.QueueId, this.hiddenBuiltInQueueId, StringComparison.Ordinal))
            .ToArray();

    private AgentInputQueuesSnapshot CurrentSnapshot
    {
        get
        {
            var source = this.inputQueues.Snapshot;
            return this.acknowledgedSnapshot is { } acknowledged
                && acknowledged.Revision >= source.Revision
                    ? acknowledged
                    : source;
        }
    }

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

    // A queue command is acknowledged by an immutable result or a Changed delta. Never mutate
    // the view projection before either arrives; a remote owner may reject/cancel the command.
    internal async Task<AgentInputQueueCommandResult> ApplyAsync(
        Func<Guid, long, Task<AgentInputQueueCommandResult>> command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ct.ThrowIfCancellationRequested();
        var result = await command(Guid.NewGuid(), this.CurrentSnapshot.Revision)
            .ConfigureAwait(false);
        if (result.Status == AgentInputQueueCommandStatus.Conflict
            && result.CurrentSnapshot is { } currentSnapshot)
        {
            await this.RefreshOnForegroundAsync(() =>
                {
                    if (currentSnapshot.Revision >= this.CurrentSnapshot.Revision)
                        this.acknowledgedSnapshot = currentSnapshot;
                })
                .ConfigureAwait(false);
        }
        else
        {
            // Remote adapters may apply the owner delta before command completion; local adapters
            // can report it through Changed. Either way, refresh only from an acknowledged source.
            await this.RefreshOnForegroundAsync().ConfigureAwait(false);
        }
        return result;
    }

    private AgentInputQueueCommandResult RejectedResult() => new()
    {
        CommandId = Guid.Empty,
        Status = AgentInputQueueCommandStatus.Rejected,
        Revision = this.CurrentSnapshot.Revision,
        ErrorCode = AgentInputQueueErrorCodes.InvalidRequest,
    };

    private static bool IsExpectedQueueOperationFailure(Exception exception) =>
        exception is OperationCanceledException
            or ObjectDisposedException
            or InvalidOperationException
            or Phantom.Workspaces.Llm.Remote.RemoteAgentSessionException
            or Phantom.Workspaces.Llm.Remote.RemoteAgentProtocolException;
}
