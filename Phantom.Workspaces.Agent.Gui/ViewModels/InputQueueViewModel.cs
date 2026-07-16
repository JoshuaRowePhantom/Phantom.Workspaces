using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    private readonly AgentChat agentChat;
    private readonly AgentInputQueueManager? inputQueueManager;
    private readonly Dictionary<AgentChatQueue, InputQueueGroupViewModel> queueViewModels = [];
    private readonly InputQueueCollectionTransformer queueCollectionTransformer;
    private readonly List<AgentChatQueue> queueUseHistory = [];
    private bool hasMultipleQueues;
    private readonly ICommand holdAllQueuesCommand;
    private readonly ICommand unholdAllQueuesCommand;
    private readonly ICommand toggleHoldAllQueuesCommand;
    private readonly ICommand submitToMostRecentQueueCommand;
    private readonly ICommand submitToNewQueueCommand;
    private readonly ICommand createNewQueueCommand;

    public InputQueueViewModel(
        AgentChat agentChat,
        AgentChatQueue defaultInputQueue,
        AgentInputQueueManager? inputQueueManager = null)
    {
        this.agentChat = agentChat;
        this.DefaultInputQueue = defaultInputQueue;
        this.inputQueueManager = inputQueueManager;
        this.DefaultComposer = new QueueComposerViewModel(this, defaultInputQueue, isDefaultComposer: true);
        this.SubmitToDefaultQueueCommand = this.DefaultComposer.SubmitCommand;
        this.holdAllQueuesCommand = new RelayCommand(this.HoldAllQueues);
        this.unholdAllQueuesCommand = new RelayCommand(this.UnholdAllQueues);
        this.toggleHoldAllQueuesCommand = new RelayCommand(this.ToggleHoldAllQueues);
        this.submitToMostRecentQueueCommand = new RelayCommand(() => this.SubmitToMostRecentQueue());
        this.submitToNewQueueCommand = new RelayCommand(() => this.SubmitToNewQueue());
        this.createNewQueueCommand = new RelayCommand(this.CreateNewQueue);
        this.queueCollectionTransformer = new InputQueueCollectionTransformer(this, this.agentChat.InputQueues, this.Queues, this.queueViewModels);
    }

    public AgentChatQueue DefaultInputQueue { get; }

    public QueueComposerViewModel DefaultComposer { get; }

    public bool HasQueueManager => this.inputQueueManager is not null;

    public bool HasMultipleQueues
    {
        get => this.hasMultipleQueues;
        private set => this.SetProperty(ref this.hasMultipleQueues, value);
    }

    public ObservableCollection<InputQueueGroupViewModel> Queues { get; } = [];

    public ReadOnlyObservableCollection<AgentChatQueue> InputQueues => this.agentChat.InputQueues;

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
        if (!this.HasQueueManager)
        {
            return this.DefaultComposer.Submit(this.DefaultInputQueue);
        }

        var queue = this.queueUseHistory.FirstOrDefault(q => q != this.DefaultInputQueue);
        if (queue is not null)
        {
            return this.DefaultComposer.Submit(queue);
        }

        if (this.DefaultInputQueue.IsImmediate)
        {
            var newQueue = this.agentChat.QueueManager.CreateInputQueue(
                immediacy: this.InputQueues.All(q => q.IsHeld)
                    ? AgentInputQueueImmediacy.Held
                    : AgentInputQueueImmediacy.Queue);
            this.RecordQueueUse(newQueue);
            return this.DefaultComposer.Submit(newQueue);
        }

        return this.DefaultComposer.Submit(this.DefaultInputQueue);
    }

    public bool SubmitToNewQueue()
    {
        if (!this.HasQueueManager)
        {
            return this.DefaultComposer.Submit(this.DefaultInputQueue);
        }

        if (string.IsNullOrWhiteSpace(this.InputText))
        {
            return false;
        }

        // Ctrl+Shift+Q always stages the new queue in the Held state so the user can configure,
        // reorder, or release it before any work is dispatched (issue #1070).
        var queue = this.agentChat.QueueManager.CreateInputQueue(
            immediacy: AgentInputQueueImmediacy.Held);
        return this.DefaultComposer.Submit(queue);
    }

    public void CreateNewQueue()
    {
        if (!this.HasQueueManager)
        {
            return;
        }

        var queue = this.agentChat.QueueManager.CreateInputQueue(
            immediacy: this.InputQueues.All(queue => queue.IsHeld)
                ? AgentInputQueueImmediacy.Held
                : AgentInputQueueImmediacy.Queue);
        this.RecordQueueUse(queue);
    }

    public void ToggleHoldAllQueues()
    {
        if (!this.HasQueueManager || this.InputQueues.Count == 0)
        {
            return;
        }

        var holdAll = this.InputQueues.Any(queue => !queue.IsHeld);
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

    public void SetQueueImmediacy(AgentChatQueue queue, AgentInputQueueImmediacy immediacy)
    {
        this.agentChat.QueueManager.SetQueueImmediacy(queue, immediacy);
        this.RefreshQueue(queue);
    }

    public void Dispose()
    {
        this.queueCollectionTransformer.Dispose();
        foreach (var queue in this.queueViewModels.Keys)
        {
            queue.Changed -= this.OnQueueChanged;
        }

        foreach (var viewModel in this.queueViewModels.Values)
        {
            viewModel.Dispose();
        }
    }

    public void RemoveQueueItem(AgentChatQueue queue, int index)
    {
        if (!this.agentChat.QueueManager.RemoveQueueItem(queue, index))
        {
            return;
        }

        this.RefreshQueue(queue);
    }

    public void RemoveQueueItem(AgentChatQueue queue, AgentInputItem item)
    {
        if (!this.agentChat.QueueManager.RemoveQueueItem(queue, item))
        {
            return;
        }

        this.RefreshQueue(queue);
    }

    public bool RemoveInputQueue(AgentChatQueue queue)
    {
        if (!this.agentChat.QueueManager.RemoveInputQueue(queue))
        {
            return false;
        }

        this.queueUseHistory.Remove(queue);
        return true;
    }

    private void RecordQueueUse(AgentChatQueue queue)
    {
        this.queueUseHistory.Remove(queue);
        this.queueUseHistory.Insert(0, queue);
    }

    public void UpdateQueueItem(AgentChatQueue queue, int index, string text)
        => this.UpdateQueueItem(queue, queue.Items[index], text);

    public void UpdateQueueItem(AgentChatQueue queue, AgentInputItem item, string text)
    {
        if (!this.agentChat.QueueManager.UpdateQueueItem(queue, item, text))
        {
            return;
        }

        this.RefreshQueue(queue);
    }

    public void RemoveQueueItemContent(AgentChatQueue queue, int index, int contentIndex)
        => this.RemoveQueueItemContent(queue, queue.Items[index], contentIndex);

    public void RemoveQueueItemContent(AgentChatQueue queue, AgentInputItem item, int contentIndex)
    {
        if (!this.agentChat.QueueManager.RemoveQueueItemContent(queue, item, contentIndex))
        {
            return;
        }

        this.RefreshQueue(queue);
    }

    public void AppendToQueue(AgentChatQueue queue, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        this.agentChat.EnqueueUserMessage(text, queue);
        this.RecordQueueUse(queue);
        this.RefreshQueue(queue);
    }

    public void AppendToQueue(AgentChatQueue queue, IReadOnlyList<AIContent> contents)
    {
        if (contents.Count == 0)
        {
            return;
        }

        this.agentChat.EnqueueUserContents(contents, queue);
        this.RecordQueueUse(queue);
        this.RefreshQueue(queue);
    }

    public void HideQueueComposer(AgentChatQueue queue)
    {
        if (this.queueViewModels.TryGetValue(queue, out var viewModel))
        {
            viewModel.HideComposer();
        }
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        if (sender is not AgentChatQueue queue)
        {
            return;
        }

        this.RefreshQueue(queue);
    }

    private void RefreshQueue(AgentChatQueue queue)
    {
        if (!this.queueViewModels.TryGetValue(queue, out var viewModel))
        {
            return;
        }

        viewModel.Refresh();
    }

    private void UpdateQueueCollectionState()
    {
        this.HasMultipleQueues = this.Queues.Count > 1;
        foreach (var queueViewModel in this.queueViewModels.Values)
        {
            queueViewModel.Refresh();
        }
    }

    private void SetAllQueuesHeld(bool held)
    {
        if (!this.HasQueueManager || this.InputQueues.Count == 0)
        {
            return;
        }

        foreach (var queue in this.InputQueues)
        {
            this.agentChat.QueueManager.SetQueueHeld(queue, held);
        }

        foreach (var queue in this.InputQueues)
        {
            this.RefreshQueue(queue);
        }
    }

    private sealed class InputQueueCollectionTransformer : CollectionTransformer<AgentChatQueue, InputQueueGroupViewModel>
    {
        private readonly InputQueueViewModel parent;
        private readonly Dictionary<AgentChatQueue, InputQueueGroupViewModel> queueViewModels;

        public InputQueueCollectionTransformer(
            InputQueueViewModel parent,
            IReadOnlyList<AgentChatQueue> source,
            IList<InputQueueGroupViewModel> target,
            Dictionary<AgentChatQueue, InputQueueGroupViewModel> queueViewModels)
            : base(source, target)
        {
            this.parent = parent;
            this.queueViewModels = queueViewModels;
            this.ApplyInitialTransform();
        }

        protected override InputQueueGroupViewModel Create(AgentChatQueue sourceItem)
        {
            var composer = sourceItem.IsDefault
                ? this.parent.DefaultComposer
                : new QueueComposerViewModel(this.parent, sourceItem, isDefaultComposer: false);
            return new InputQueueGroupViewModel(this.parent, sourceItem, composer);
        }

        protected override void OnInsert(int index, InputQueueGroupViewModel target)
        {
            target.Queue.Changed += this.parent.OnQueueChanged;
            this.queueViewModels[target.Queue] = target;
            this.parent.UpdateQueueCollectionState();
        }

        protected override void OnRemoveAt(int index, InputQueueGroupViewModel target)
        {
            target.Queue.Changed -= this.parent.OnQueueChanged;
            this.queueViewModels.Remove(target.Queue);
            target.Dispose();
            this.parent.UpdateQueueCollectionState();
        }
    }
}
