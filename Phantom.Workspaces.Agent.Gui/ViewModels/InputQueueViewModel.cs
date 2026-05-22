using System.Collections.ObjectModel;
using System.Windows.Input;
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
    private AgentChatQueue? mostRecentlyCreatedQueue;
    private string inputText = string.Empty;
    private bool isFormattedMode;

    public InputQueueViewModel(
        AgentChat agentChat,
        AgentChatQueue defaultInputQueue,
        AgentInputQueueManager? inputQueueManager = null)
    {
        this.agentChat = agentChat;
        this.DefaultInputQueue = defaultInputQueue;
        this.inputQueueManager = inputQueueManager;
        this.SubmitToDefaultQueueCommand = new RelayCommand(this.SubmitToDefaultQueue);
        this.SubmitToMostRecentQueueCommand = new RelayCommand(this.SubmitToMostRecentQueue);
        this.SubmitToNewQueueCommand = new RelayCommand(this.SubmitToNewQueue);
    }

    public AgentChatQueue DefaultInputQueue { get; }

    public bool HasQueueManager => this.inputQueueManager is not null;

    public ObservableCollection<AgentChatQueue> InputQueues => this.agentChat.InputQueues;

    public string InputText
    {
        get => this.inputText;
        set => this.SetProperty(ref this.inputText, value);
    }

    /// <summary>
    /// True when the input box is in multi-line formatted mode.
    /// In this mode Enter inserts a newline; Ctrl+Enter submits.
    /// </summary>
    public bool IsFormattedMode
    {
        get => this.isFormattedMode;
        set => this.SetProperty(ref this.isFormattedMode, value);
    }

    public ICommand SubmitToDefaultQueueCommand { get; }

    public ICommand SubmitToMostRecentQueueCommand { get; }

    public ICommand SubmitToNewQueueCommand { get; }

    /// <summary>
    /// Enters formatted mode. Called on Shift+Enter in normal mode.
    /// </summary>
    public void EnterFormattedMode()
    {
        this.IsFormattedMode = true;
    }

    /// <summary>
    /// Exits formatted mode without submitting. Called on Esc in formatted mode.
    /// </summary>
    public void ExitFormattedMode()
    {
        this.IsFormattedMode = false;
    }

    /// <summary>
    /// Submits current text to the default queue and returns to normal mode.
    /// </summary>
    public void SubmitToDefaultQueue()
    {
        this.SubmitToQueue(this.DefaultInputQueue);
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

        var queue = this.agentChat.CreateInputQueue();
        this.mostRecentlyCreatedQueue = queue;
        this.SubmitToQueue(queue);
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
    }
}
