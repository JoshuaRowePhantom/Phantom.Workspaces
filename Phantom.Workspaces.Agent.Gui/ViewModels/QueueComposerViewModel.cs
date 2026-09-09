using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class QueueComposerViewModel : ViewModelBase, IQueueImmediacyViewModel
{
    private readonly InputQueueViewModel parent;
    private readonly string targetQueueId;
    private readonly List<AIContent> attachments = [];
    private readonly List<string> attachmentPlaceholders = [];
    private readonly ObservableCollection<QueueComposerAttachmentViewModel> attachmentPreviews = [];
    private readonly List<string> inputHistory = [];
    private int historyIndex = -1;
    private string savedDraft = string.Empty;
    private int savedDraftCaretIndex;
    private string inputText = string.Empty;
    private bool isFormattedMode;
    private bool showChatInputHelpText = true;
    private CancellationTokenSource? completionsCts;

    /// <summary>
    /// When set, called with the raw input text when the user submits text starting with '/'.
    /// The interceptor is responsible for executing the slash command (or showing an error
    /// for unknown commands). The message is never forwarded to the agent queue when this
    /// interceptor is set and the input starts with '/'.
    /// </summary>
    public Func<string, Task>? SlashCommandInterceptorAsync { get; set; }

    /// <summary>
    /// When set, called with (commandName, partialArguments, cancellationToken) whenever
    /// <see cref="InputText"/> starts with '/' to populate the completions popup.
    /// </summary>
    public Func<string, string, CancellationToken, Task<IReadOnlyList<SlashCommandCompletion>>>? SlashCompletionsProviderAsync { get; set; }
    internal Action? HideOwnerComposerAction { get; set; }

    /// <summary>Completions popup state driven by <see cref="InputText"/> changes.</summary>
    public SlashCompletionsViewModel Completions { get; } = new();

    public QueueComposerViewModel(
        InputQueueViewModel parent,
        string targetQueueId,
        bool isDefaultComposer)
    {
        this.parent = parent;
        this.targetQueueId = targetQueueId;
        this.IsDefaultComposer = isDefaultComposer;
        this.SubmitCommand = new RelayCommand(this.Submit);
        this.SubmitToNewQueueCommand = new RelayCommand(() => this.SubmitToNewQueue());
        this.CreateNewQueueCommand = new RelayCommand(this.CreateNewQueue);
        this.SetImmediacyCommand = new RelayCommand<QueueImmediacyOption>(this.SetQueueImmediacy);
    }

    public QueueComposerViewModel(
        InputQueueViewModel parent,
        AgentChatQueue targetQueue,
        bool isDefaultComposer)
        : this(
            parent,
            targetQueue.IsDefault
                ? parent.DefaultQueueId
                : targetQueue.IsImmediate
                    ? parent.InputQueues.First(static queue => queue.IsImmediate).QueueId
                    : parent.InputQueues.First(queue => string.Equals(queue.Name, targetQueue.Name, StringComparison.Ordinal)).QueueId,
            isDefaultComposer)
    {
    }

    public event EventHandler? FocusPrimaryControlRequested;

    public void RequestFocusPrimaryControl() =>
        FocusPrimaryControlRequested?.Invoke(this, EventArgs.Empty);

    public bool IsDefaultComposer { get; }

    public string PlaceholderText => this.IsDefaultComposer
        ? (this.isFormattedMode
            ? "Multi-line mode"
            : "Type a message…  (Enter · send  |  Shift+Enter · multi-line  |  Ctrl+Q · enqueue)")
        : "Append to this queue...";

    public string? FormattedModeHint =>
        this.isFormattedMode && this.IsDefaultComposer
            ? "Ctrl+Enter · send   Enter · new line   Esc · exit multi-line   Ctrl-Shift-Enter - submit before cursor"
            : null;

    public string? NormalModeHint =>
        !this.isFormattedMode && this.IsDefaultComposer
            ? "Enter · send  ·  Shift+Enter · multi-line  ·  Ctrl+Q · enqueue  ·  Ctrl+Shift+Q · new queue  ·  Ctrl+Break · interrupt  ·  Pause · toggle hold  ·  Shift+Pause · hold all  ·  Ctrl-Shift-Enter - submit before cursor"
            : null;

    public string? ActiveHint => this.FormattedModeHint ?? this.NormalModeHint;

    public bool ShowChatInputHelpText
    {
        get => this.showChatInputHelpText;
        set
        {
            if (this.SetProperty(ref this.showChatInputHelpText, value))
            {
                this.RaisePropertyChanged(nameof(this.ShowHintText));
            }
        }
    }

    public bool ShowHintText => !string.IsNullOrEmpty(this.ActiveHint) && this.showChatInputHelpText;

    public string SubmitButtonText => this.IsDefaultComposer ? "Send" : "Add";

    public string SubmitButtonGlyph => "↵";

    public QueueImmediacyOption SelectedImmediacyOption => QueueImmediacyOption.All.First(option => option.Value == this.TargetQueueSnapshot.Immediacy);

    public QueueImmediacyOption ImmediateImmediacyOption => QueueImmediacyOption.All[0];

    public QueueImmediacyOption QueuedImmediacyOption => QueueImmediacyOption.All[1];

    public QueueImmediacyOption HeldImmediacyOption => QueueImmediacyOption.All[2];

    public bool CanCreateQueues => this.IsDefaultComposer;

    public bool HasAttachments => this.attachments.Count > 0;

    public ObservableCollection<QueueComposerAttachmentViewModel> AttachmentPreviews => this.attachmentPreviews;

    public string InputText
    {
        get => this.inputText;
        set
        {
            if (this.SetProperty(ref this.inputText, value))
            {
                this.OnInputTextChanged(value);
            }
        }
    }

    public bool IsFormattedMode
    {
        get => this.isFormattedMode;
        set
        {
            if (this.SetProperty(ref this.isFormattedMode, value))
            {
                this.RaisePropertyChanged(nameof(this.PlaceholderText));
                this.RaisePropertyChanged(nameof(this.FormattedModeHint));
                this.RaisePropertyChanged(nameof(this.NormalModeHint));
                this.RaisePropertyChanged(nameof(this.ActiveHint));
                this.RaisePropertyChanged(nameof(this.ShowHintText));
            }
        }
    }

    public ICommand SubmitCommand { get; }

    public ICommand SubmitToNewQueueCommand { get; }

    public ICommand CreateNewQueueCommand { get; }

    public ICommand SetImmediacyCommand { get; }

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
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
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

    public bool TryNavigateHistoryUp(int caretLine, out string text, out int caretIndex)
    {
        text = string.Empty;
        caretIndex = 0;

        if (caretLine != 0)
        {
            return false;
        }

        if (this.inputHistory.Count == 0)
        {
            return false;
        }

        if (this.historyIndex == -1)
        {
            this.savedDraft = this.InputText;
            this.savedDraftCaretIndex = this.InputText.Length;
            this.historyIndex = this.inputHistory.Count - 1;
        }
        else if (this.historyIndex > 0)
        {
            this.historyIndex--;
        }

        text = this.inputHistory[this.historyIndex];
        caretIndex = 0;
        return true;
    }

    public bool TryNavigateHistoryDown(out string text, out int caretIndex)
    {
        text = string.Empty;
        caretIndex = 0;

        if (this.historyIndex == -1)
        {
            return false;
        }

        if (this.historyIndex < this.inputHistory.Count - 1)
        {
            this.historyIndex++;
            text = this.inputHistory[this.historyIndex];
            caretIndex = 0;
        }
        else
        {
            this.historyIndex = -1;
            text = this.savedDraft;
            caretIndex = this.savedDraftCaretIndex;
        }

        return true;
    }

    public void CommitToHistory(string text)
    {
        this.ResetHistoryNavigation();

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (this.inputHistory.Count > 0
            && string.Equals(this.inputHistory[^1], text, StringComparison.Ordinal))
        {
            return;
        }

        this.inputHistory.Add(text);
    }

    private void ResetHistoryNavigation()
    {
        this.historyIndex = -1;
        this.savedDraft = string.Empty;
        this.savedDraftCaretIndex = 0;
    }

    public void Submit()
    {
        this.Submit(this.targetQueueId);
    }

    public bool Submit(string targetQueueId)
    {
        var text = this.SanitizeText(this.InputText);
        if (string.IsNullOrWhiteSpace(text) && this.attachments.Count == 0)
        {
            return false;
        }

        this.SubmitContent(text, targetQueueId);
        this.InputText = string.Empty;
        return true;
    }

    public bool SubmitBeforeCursor(int caretIndex) =>
        this.SubmitBeforeCursor(this.targetQueueId, caretIndex, out _);

    public bool SubmitBeforeCursor(int caretIndex, out int newCaretIndex) =>
        this.SubmitBeforeCursor(this.targetQueueId, caretIndex, out newCaretIndex);

    public bool SubmitBeforeCursor(string targetQueueId, int caretIndex, out int newCaretIndex)
    {
        var input = this.InputText ?? string.Empty;
        var clampedCaret = Math.Clamp(caretIndex, 0, input.Length);
        newCaretIndex = clampedCaret;

        var before = input.Substring(0, clampedCaret);
        var after = input.Substring(clampedCaret);

        var text = this.SanitizeText(before);
        if (string.IsNullOrWhiteSpace(text) && this.attachments.Count == 0)
        {
            return false;
        }

        this.SubmitContent(text, targetQueueId);

        // Retain everything after the caret and place the caret at the start of it,
        // rather than hard-clearing the box the way Submit does.
        this.InputText = after;
        newCaretIndex = 0;
        return true;
    }

    /// <summary>
    /// Runs the shared submit pipeline for <paramref name="text"/> (slash-command
    /// interception on the default composer, history commit, queue append, attachments)
    /// without clearing the input box — the caller decides what the box becomes.
    /// </summary>
    private void SubmitContent(string text, string targetQueueId)
    {
        // Intercept slash commands on the default (primary) composer. Non-default queue
        // composers are used to append steering messages; slash commands are not applicable there.
        if (this.IsDefaultComposer
            && text.StartsWith('/')
            && this.attachments.Count == 0
            && this.SlashCommandInterceptorAsync is { } interceptor)
        {
            this.ResetHistoryNavigation();
            _ = interceptor(text);
            return;
        }

        var contents = new List<AIContent>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            contents.Add(new TextContent(text));
        }

        contents.AddRange(this.attachments);
        this.CommitToHistory(text);
        this.parent.AppendToQueue(targetQueueId, contents);
        this.ClearAttachments();
        this.IsFormattedMode = false;
        if (!this.IsDefaultComposer)
        {
            this.HideOwnerComposerAction?.Invoke();
            this.parent.HideQueueComposer(targetQueueId);
        }
    }

    public bool SubmitToMostRecentQueue()
    {
        if (this.IsDefaultComposer)
        {
            return this.parent.SubmitToMostRecentQueue();
        }

        return false;
    }

    public bool SubmitToNewQueue()
    {
        if (this.IsDefaultComposer)
        {
            return this.parent.SubmitToNewQueue();
        }

        return false;
    }

    public void CreateNewQueue()
    {
        if (this.IsDefaultComposer)
        {
            this.parent.CreateNewQueue();
        }
    }

    public void ToggleHoldAllQueues()=> this.parent.ToggleHoldAllQueues();

    public void HoldAllQueues() => this.parent.HoldAllQueues();

    public void UnholdAllQueues() => this.parent.UnholdAllQueues();

    public void Dispose()
    {
        this.completionsCts?.Cancel();
        this.completionsCts?.Dispose();
        this.completionsCts = null;
        this.ClearAttachments();
    }

    private void OnInputTextChanged(string text)
    {
        this.completionsCts?.Cancel();
        this.completionsCts?.Dispose();
        this.completionsCts = null;

        if (!this.IsDefaultComposer
            || !text.StartsWith('/')
            || this.SlashCompletionsProviderAsync is not { } provider)
        {
            this.Completions.Dismiss();
            return;
        }

        var withoutSlash = text.Substring(1);
        var spaceIndex = withoutSlash.IndexOf(' ');

        // When there is no space yet the user is still typing the command name.
        // Pass commandName="" as a sentinel so the provider can return root (command-list) completions.
        // When a space is present the command name is resolved; pass it together with the partial args.
        string commandName, partialArgs;
        if (spaceIndex < 0)
        {
            commandName = string.Empty;
            partialArgs = withoutSlash;
        }
        else
        {
            commandName = withoutSlash[..spaceIndex];
            partialArgs = withoutSlash[(spaceIndex + 1)..];
        }

        var cts = new CancellationTokenSource();
        this.completionsCts = cts;
        _ = this.FetchAndApplyCompletionsAsync(provider, commandName, partialArgs, cts.Token);
    }

    private async Task FetchAndApplyCompletionsAsync(
        Func<string, string, CancellationToken, Task<IReadOnlyList<SlashCommandCompletion>>> provider,
        string commandName,
        string partialArgs,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SlashCommandCompletion> completions;
        try
        {
            completions = await provider(commandName, partialArgs, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            this.Completions.SetItems(completions);
        }
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

    public void RefreshQueueState()
    {
        this.RaisePropertyChanged(nameof(this.SelectedImmediacyOption));
    }

    private void SetQueueImmediacy(QueueImmediacyOption option)
    {
        this.parent.SetQueueImmediacy(this.targetQueueId, option.Value);
    }

    private AgentInputQueueSnapshot TargetQueueSnapshot
        => this.parent.TryGetQueueSnapshot(this.targetQueueId, out var snapshot)
            ? snapshot
            : throw new InvalidOperationException($"Queue '{this.targetQueueId}' is no longer available.");
}
