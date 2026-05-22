using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.AI;
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
    private AgentChatQueue? mostRecentlyCreatedQueue;
    private bool hasMultipleQueues;
    private readonly ICommand submitToMostRecentQueueCommand;
    private readonly ICommand submitToNewQueueCommand;

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
        this.submitToMostRecentQueueCommand = new RelayCommand(this.SubmitToMostRecentQueue);
        this.submitToNewQueueCommand = new RelayCommand(this.SubmitToNewQueue);
        this.agentChat.InputQueues.CollectionChanged += this.OnInputQueuesChanged;
        this.RebuildQueues();
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

    public ObservableCollection<AgentChatQueue> InputQueues => this.agentChat.InputQueues;

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

    public ICommand SubmitToMostRecentQueueCommand => this.submitToMostRecentQueueCommand;

    public ICommand SubmitToNewQueueCommand => this.submitToNewQueueCommand;

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

    public void SubmitToMostRecentQueue()
    {
        if (!this.HasQueueManager)
        {
            this.SubmitToDefaultQueue();
            return;
        }

        var queue = this.mostRecentlyCreatedQueue ?? this.DefaultInputQueue;
        this.SubmitToQueue(queue);
    }

    public void SubmitToNewQueue()
    {
        if (!this.HasQueueManager)
        {
            this.SubmitToDefaultQueue();
            return;
        }

        if (string.IsNullOrWhiteSpace(this.InputText))
        {
            return;
        }

        var queue = this.agentChat.CreateInputQueue(
            immediacy: this.InputQueues.All(queue => queue.IsHeld)
                ? AgentInputQueueImmediacy.Held
                : AgentInputQueueImmediacy.Queue);
        this.mostRecentlyCreatedQueue = queue;
        this.SubmitToQueue(queue);
        this.RebuildQueues();
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
        this.agentChat.SetQueueImmediacy(queue, immediacy);
        this.RefreshQueue(queue);
    }

    public void Dispose()
    {
        this.agentChat.InputQueues.CollectionChanged -= this.OnInputQueuesChanged;
        foreach (var queue in this.queueViewModels.Keys)
        {
            queue.Changed -= this.OnQueueChanged;
        }
    }

    public void RemoveQueueItem(AgentChatQueue queue, int index)
    {
        if (!this.agentChat.RemoveQueueItem(queue, index))
        {
            return;
        }

        this.RefreshQueue(queue);
    }

    public void UpdateQueueItem(AgentChatQueue queue, int index, string text)
    {
        if (!this.agentChat.UpdateQueueItem(queue, index, text))
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
        this.RefreshQueue(queue);
    }

    public void AppendToQueue(AgentChatQueue queue, IReadOnlyList<AIContent> contents)
    {
        if (contents.Count == 0)
        {
            return;
        }

        this.agentChat.EnqueueUserContents(contents, queue);
        this.RefreshQueue(queue);
    }

    public void HideQueueComposer(AgentChatQueue queue)
    {
        if (this.queueViewModels.TryGetValue(queue, out var viewModel))
        {
            viewModel.HideComposer();
        }
    }

    private void OnInputQueuesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(this.RebuildQueues);
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        if (sender is not AgentChatQueue queue)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => this.RefreshQueue(queue));
    }

    private void RebuildQueues()
    {
        foreach (var queue in this.queueViewModels.Keys.ToArray())
        {
            queue.Changed -= this.OnQueueChanged;
        }

        this.queueViewModels.Clear();
        this.Queues.Clear();

        foreach (var queue in this.agentChat.InputQueues)
        {
            queue.Changed += this.OnQueueChanged;
            var composer = queue.IsDefault
                ? this.DefaultComposer
                : new QueueComposerViewModel(this, queue, isDefaultComposer: false);
            var vm = new InputQueueGroupViewModel(this, queue, composer);
            this.queueViewModels[queue] = vm;
            this.Queues.Add(vm);
        }

        this.HasMultipleQueues = this.Queues.Count > 1;
    }

    private void RefreshQueue(AgentChatQueue queue)
    {
        if (!this.queueViewModels.TryGetValue(queue, out var viewModel))
        {
            this.RebuildQueues();
            return;
        }

        viewModel.Refresh();
    }

    private void SetAllQueuesHeld(bool held)
    {
        if (!this.HasQueueManager || this.InputQueues.Count == 0)
        {
            return;
        }

        foreach (var queue in this.InputQueues)
        {
            this.agentChat.SetQueueHeld(queue, held);
        }

        this.RebuildQueues();
    }

    private void SubmitToQueue(AgentChatQueue queue)
    {
        var text = this.InputText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        this.agentChat.EnqueueUserContents([new TextContent(text)], queue);
        this.InputText = string.Empty;
        this.IsFormattedMode = false;
        this.RefreshQueue(queue);
    }
}
