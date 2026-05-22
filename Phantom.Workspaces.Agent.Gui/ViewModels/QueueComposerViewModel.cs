using System.IO;
using System.Windows.Input;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class QueueComposerViewModel : ViewModelBase
{
    private readonly InputQueueViewModel parent;
    private readonly AgentChatQueue targetQueue;
    private readonly List<AIContent> attachments = [];
    private string inputText = string.Empty;
    private bool isFormattedMode;

    public QueueComposerViewModel(
        InputQueueViewModel parent,
        AgentChatQueue targetQueue,
        bool isDefaultComposer)
    {
        this.parent = parent;
        this.targetQueue = targetQueue;
        this.IsDefaultComposer = isDefaultComposer;
        this.SubmitCommand = new RelayCommand(this.Submit);
    }

    public bool IsDefaultComposer { get; }

    public string PlaceholderText => this.IsDefaultComposer
        ? "Type a message…  (Enter to send, Shift+Enter for multi-line)"
        : "Append to this queue...";

    public string SubmitButtonText => this.IsDefaultComposer ? "Send" : "Add";

    public bool CanCreateQueues => this.IsDefaultComposer;

    public bool HasAttachments => this.attachments.Count > 0;

    public string InputText
    {
        get => this.inputText;
        set => this.SetProperty(ref this.inputText, value);
    }

    public bool IsFormattedMode
    {
        get => this.isFormattedMode;
        set => this.SetProperty(ref this.isFormattedMode, value);
    }

    public ICommand SubmitCommand { get; }

    public void EnterFormattedMode() => this.IsFormattedMode = true;

    public void ExitFormattedMode() => this.IsFormattedMode = false;

    public void AppendImageAttachment(byte[] imageData, string mediaType, int width, int height, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        this.attachments.Add(new DataContent(imageData, mediaType));

        var placeholder = this.FormatImagePlaceholder(width, height, fileName);
        if (!string.IsNullOrWhiteSpace(this.InputText))
        {
            this.InputText += this.InputText.EndsWith(' ') ? string.Empty : " ";
        }

        this.InputText += placeholder;
        this.RaisePropertyChanged(nameof(this.HasAttachments));
    }

    public void Submit()
    {
        var text = this.InputText;
        if (string.IsNullOrWhiteSpace(text) && this.attachments.Count == 0)
        {
            return;
        }

        var contents = new List<AIContent>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            contents.Add(new TextContent(text));
        }

        contents.AddRange(this.attachments);
        this.parent.AppendToQueue(this.targetQueue, contents);
        this.InputText = string.Empty;
        this.attachments.Clear();
        this.RaisePropertyChanged(nameof(this.HasAttachments));
        this.IsFormattedMode = false;
        if (!this.IsDefaultComposer)
        {
            this.parent.HideQueueComposer(this.targetQueue);
        }
    }

    public void SubmitToMostRecentQueue()
    {
        if (this.IsDefaultComposer)
        {
            this.parent.SubmitToMostRecentQueue();
        }
    }

    public void SubmitToNewQueue()
    {
        if (this.IsDefaultComposer)
        {
            this.parent.SubmitToNewQueue();
        }
    }

    public void ToggleHoldAllQueues() => this.parent.ToggleHoldAllQueues();

    public void HoldAllQueues() => this.parent.HoldAllQueues();

    public void UnholdAllQueues() => this.parent.UnholdAllQueues();

    private string FormatImagePlaceholder(int width, int height, string? fileName)
    {
        var size = $"{width}x{height}";
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return $"[image {size}]";
        }

        return $"[image {size} {fileName}]";
    }
}
