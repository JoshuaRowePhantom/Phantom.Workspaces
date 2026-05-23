using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class QueueComposerViewModel : ViewModelBase
{
    private readonly InputQueueViewModel parent;
    private readonly AgentChatQueue targetQueue;
    private readonly List<AIContent> attachments = [];
    private readonly List<string> attachmentPlaceholders = [];
    private readonly ObservableCollection<QueueComposerAttachmentViewModel> attachmentPreviews = [];
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
        this.targetQueue.Changed += this.OnTargetQueueChanged;
        this.SubmitCommand = new RelayCommand(this.Submit);
        this.SetQueueImmediacyCommand = new RelayCommand<QueueImmediacyOption>(this.SetQueueImmediacy);
    }

    public bool IsDefaultComposer { get; }

    public string PlaceholderText => this.IsDefaultComposer
        ? "Type a message…  (Enter to send, Shift+Enter for multi-line)"
        : "Append to this queue...";

    public string SubmitButtonText => this.IsDefaultComposer ? "Send" : "Add";

    public string SubmitButtonGlyph => "↵";

    public QueueImmediacyOption SubmitStatusOption => QueueImmediacyOption.All.First(option => option.Value == this.targetQueue.Immediacy);

    public QueueImmediacyOption ImmediateImmediacyOption => QueueImmediacyOption.All[0];

    public QueueImmediacyOption QueuedImmediacyOption => QueueImmediacyOption.All[1];

    public QueueImmediacyOption HeldImmediacyOption => QueueImmediacyOption.All[2];

    public bool CanCreateQueues => this.IsDefaultComposer;

    public bool HasAttachments => this.attachments.Count > 0;

    public ObservableCollection<QueueComposerAttachmentViewModel> AttachmentPreviews => this.attachmentPreviews;

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

    public ICommand SetQueueImmediacyCommand { get; }

    public void EnterFormattedMode() => this.IsFormattedMode = true;

    public void ExitFormattedMode() => this.IsFormattedMode = false;

    public void AppendImageAttachment(byte[] imageData, string mediaType, int width, int height, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(imageData);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        this.attachments.Add(new DataContent(imageData, mediaType));

        var placeholder = this.FormatImagePlaceholder(width, height, fileName);
        this.attachmentPlaceholders.Add(placeholder);
        Bitmap? preview = null;

        try
        {
            preview = new Bitmap(new MemoryStream(imageData));
        }
        catch (InvalidOperationException)
        {
            // Tests can run without an Avalonia render backend.
        }

        this.attachmentPreviews.Add(new QueueComposerAttachmentViewModel(
            preview,
            placeholder,
            new RelayCommand<QueueComposerAttachmentViewModel>(this.RemoveAttachment)));
        if (!string.IsNullOrWhiteSpace(this.InputText))
        {
            this.InputText += this.InputText.EndsWith(' ') ? string.Empty : " ";
        }

        this.InputText += placeholder;
        this.RaisePropertyChanged(nameof(this.HasAttachments));
    }

    public bool TryRemoveImageAttachmentBeforeCaret(
        string text,
        int caretIndex,
        out string updatedText,
        out int updatedCaretIndex)
    {
        updatedText = text;
        updatedCaretIndex = caretIndex;

        if (caretIndex <= 0 || caretIndex > text.Length || this.attachmentPlaceholders.Count == 0)
        {
            return false;
        }

        for (var index = this.attachmentPlaceholders.Count - 1; index >= 0; index--)
        {
            var placeholder = this.attachmentPlaceholders[index];
            var startIndex = caretIndex - placeholder.Length;
            if (startIndex < 0)
            {
                continue;
            }

            if (!string.Equals(text.Substring(startIndex, placeholder.Length), placeholder, StringComparison.Ordinal))
            {
                continue;
            }

            var removeStart = startIndex;
            if (removeStart > 0 && text[removeStart - 1] == ' ')
            {
                removeStart--;
            }

            updatedText = text.Remove(removeStart, caretIndex - removeStart);
            updatedCaretIndex = removeStart;
            this.RemoveAttachmentAt(index);
            this.InputText = updatedText;
            this.RaisePropertyChanged(nameof(this.HasAttachments));
            this.RaisePropertyChanged(nameof(this.AttachmentPreviews));
            return true;
        }

        return false;
    }

    public void Submit()
    {
        var text = this.SanitizeText(this.InputText);
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
        this.ClearAttachments();
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

    public void Dispose()
    {
        this.targetQueue.Changed -= this.OnTargetQueueChanged;
        this.ClearAttachments();
    }

    private string FormatImagePlaceholder(int width, int height, string? fileName)
    {
        var size = $"{width}x{height}";
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return $"[image {size}]";
        }

        return $"[image {size} {fileName}]";
    }

    private string SanitizeText(string text)
    {
        var sanitized = text;
        foreach (var placeholder in this.attachmentPlaceholders)
        {
            sanitized = sanitized.Replace(placeholder, string.Empty, StringComparison.Ordinal);
        }

        return sanitized.TrimEnd();
    }

    private void RemoveAttachment(QueueComposerAttachmentViewModel attachment)
    {
        var index = this.attachmentPreviews.IndexOf(attachment);
        if (index < 0)
        {
            return;
        }

        var placeholder = this.RemoveAttachmentAt(index);
        this.InputText = this.RemovePlaceholderText(this.InputText, placeholder);
        this.RaisePropertyChanged(nameof(this.HasAttachments));
        this.RaisePropertyChanged(nameof(this.AttachmentPreviews));
    }

    private string RemoveAttachmentAt(int index)
    {
        var placeholder = this.attachmentPlaceholders[index];
        this.attachments.RemoveAt(index);
        this.attachmentPlaceholders.RemoveAt(index);
        var attachment = this.attachmentPreviews[index];
        this.attachmentPreviews.RemoveAt(index);
        attachment.Dispose();
        return placeholder;
    }

    private string RemovePlaceholderText(string text, string placeholder)
    {
        var index = text.IndexOf(placeholder, StringComparison.Ordinal);
        if (index < 0)
        {
            return text;
        }

        var removeStart = index;
        if (removeStart > 0 && text[removeStart - 1] == ' ')
        {
            removeStart--;
        }

        return text.Remove(removeStart, placeholder.Length + (removeStart < index ? 1 : 0)).TrimEnd();
    }

    private void ClearAttachments()
    {
        foreach (var attachment in this.attachmentPreviews)
        {
            attachment.Dispose();
        }

        this.attachments.Clear();
        this.attachmentPlaceholders.Clear();
        this.attachmentPreviews.Clear();
        this.RaisePropertyChanged(nameof(this.HasAttachments));
        this.RaisePropertyChanged(nameof(this.AttachmentPreviews));
    }

    private void OnTargetQueueChanged(object? sender, EventArgs e)
    {
        this.RaisePropertyChanged(nameof(this.SubmitStatusOption));
    }

    private void SetQueueImmediacy(QueueImmediacyOption option)
    {
        this.parent.SetQueueImmediacy(this.targetQueue, option.Value);
    }
}
