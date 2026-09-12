using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class InputQueueEntryViewModel : ViewModelBase
{
    private readonly InputQueueViewModel parent;
    private readonly string queueId;
    private bool isEditing;
    private string editText;
    private ImmutableArray<ChatMessage> messages;
    private ObservableCollection<InputQueueEntryAttachmentViewModel> attachments;

    public event EventHandler? EditStarted;

    public InputQueueEntryViewModel(
        InputQueueViewModel parent,
        string queueId,
        AgentInputItemSnapshot item)
    {
        this.parent = parent;
        this.queueId = queueId;
        this.ItemId = item.ItemId;
        this.messages = item.Messages;
        this.Text = ReadText(item.Messages);
        this.editText = this.Text;
        this.attachments = this.CreateAttachments(item.Messages);
        this.RemoveCommand = new AsyncRelayCommand(
            _ => this.parent.ExecuteQueueCommandWithFeedbackAsync(() => this.RemoveAsync()));
        this.EditCommand = new RelayCommand(this.BeginEdit);
        this.SaveEditCommand = new AsyncRelayCommand(
            _ => this.parent.ExecuteQueueOperationWithFeedbackAsync(() => this.SaveEditAsync()));
        this.CancelEditCommand = new RelayCommand(this.CancelEdit);
    }

    public string ItemId { get; }

    public string Text { get; private set; }

    public ObservableCollection<InputQueueEntryAttachmentViewModel> Attachments
    {
        get => this.attachments;
        private set => this.SetProperty(ref this.attachments, value);
    }

    public bool HasAttachments => this.Attachments.Count > 0;

    public bool IsEditing
    {
        get => this.isEditing;
        private set
        {
            if (this.SetProperty(ref this.isEditing, value))
            {
                this.RaisePropertyChanged(nameof(this.IsNotEditing));
                this.RaisePropertyChanged(nameof(this.ShowEditHint));
            }
        }
    }

    public bool IsNotEditing => !this.IsEditing;

    public bool ShowEditHint => this.IsEditing;

    public string EditShortcutHint => "Enter · save   ·   Shift+Enter · newline   ·   Ctrl+Enter · send";

    public string EditText
    {
        get => this.editText;
        set => this.SetProperty(ref this.editText, value);
    }

    public ICommand RemoveCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand SaveEditCommand { get; }

    public ICommand CancelEditCommand { get; }

    public void Refresh(AgentInputItemSnapshot item)
    {
        this.messages = item.Messages;
        this.Text = ReadText(item.Messages);
        if (!this.IsEditing)
        {
            this.EditText = this.Text;
        }
        this.RefreshAttachments(item.Messages);
        this.RaisePropertyChanged(nameof(this.Text));
    }

    private Task<AgentInputQueueCommandResult> RemoveAsync() =>
        this.parent.RemoveQueueItemAsync(this.queueId, this.ItemId);

    private void RemoveAttachment(int contentIndex) => this.parent.RemoveQueueItemContent(new RemoveQueueItemContentRequest
    {
        QueueId = this.queueId,
        ItemId = this.ItemId,
        ContentIndex = contentIndex,
    });

    private void BeginEdit()
    {
        this.EditText = this.Text;
        this.IsEditing = true;
        this.EditStarted?.Invoke(this, EventArgs.Empty);
    }

    public void SaveEdit() =>
        _ = this.parent.ExecuteQueueOperationWithFeedbackAsync(() => this.SaveEditAsync());

    public async Task SaveEditAsync(CancellationToken ct = default)
    {
        var result = await this.parent.UpdateQueueItemAsync(
            this.queueId,
            this.ItemId,
            this.EditText,
            ct);
        if (result.Status == AgentInputQueueCommandStatus.Applied)
        {
            this.IsEditing = false;
        }
        else
        {
            this.parent.ReportQueueOperationFailure();
        }
    }

    public void SaveAndSendImmediately() =>
        _ = this.parent.ExecuteQueueOperationWithFeedbackAsync(
            () => this.SaveAndSendImmediatelyAsync());

    public async Task SaveAndSendImmediatelyAsync(CancellationToken ct = default)
    {
        await this.parent.SendQueueItemImmediatelyAsync(this.queueId, this.ItemId, this.EditText, ct);
        if (!this.parent.TryGetItemSnapshot(this.queueId, this.ItemId, out _))
        {
            this.IsEditing = false;
        }
    }

    public void CancelEdit()
    {
        this.EditText = this.Text;
        this.IsEditing = false;
    }

    private ObservableCollection<InputQueueEntryAttachmentViewModel> CreateAttachments(ImmutableArray<ChatMessage> messages)
    {
        var attachments = new ObservableCollection<InputQueueEntryAttachmentViewModel>();
        var contents = GetPrimaryContents(messages);
        for (var contentIndex = 0; contentIndex < contents.Count; contentIndex++)
        {
            if (contents[contentIndex] is TextContent)
            {
                continue;
            }

            var attachmentIndex = contentIndex;
            var preview = contents[contentIndex] is DataContent dataContent
                ? this.TryCreatePreview(dataContent)
                : null;
            attachments.Add(new InputQueueEntryAttachmentViewModel(
                preview,
                this.FormatContentLabel(contents[contentIndex]),
                new RelayCommand(() => this.RemoveAttachment(attachmentIndex))));
        }

        return attachments;
    }

    private void RefreshAttachments(ImmutableArray<ChatMessage> messages)
    {
        foreach (var attachment in this.Attachments)
        {
            attachment.Dispose();
        }

        this.Attachments = this.CreateAttachments(messages);
        this.RaisePropertyChanged(nameof(this.HasAttachments));
    }

    private string FormatContentLabel(AIContent content)
        => content is DataContent dataContent
            ? (string.IsNullOrWhiteSpace(dataContent.MediaType) ? "image" : dataContent.MediaType)
            : content.GetType().Name;

    private Bitmap? TryCreatePreview(DataContent dataContent)
    {
        if (!IsImageMediaType(dataContent.MediaType))
        {
            return null;
        }

        try
        {
            return new Bitmap(new MemoryStream(dataContent.Data.ToArray()));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsImageMediaType(string? mediaType)
        => !string.IsNullOrWhiteSpace(mediaType) && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static string ReadText(ImmutableArray<ChatMessage> messages)
        => string.Concat(GetPrimaryContents(messages).OfType<TextContent>().Select(static content => content.Text));

    private static IReadOnlyList<AIContent> GetPrimaryContents(ImmutableArray<ChatMessage> messages)
        => messages.Length > 0 ? messages[0].Contents.ToArray() : [];
}
