using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class InputQueueEntryViewModel : ViewModelBase
{
    private readonly InputQueueViewModel parent;
    private readonly AgentChatQueue queue;
    private readonly AgentInputItem item;
    private bool isEditing;
    private string editText;

    public event EventHandler? EditStarted;

    public InputQueueEntryViewModel(
        InputQueueViewModel parent,
        AgentChatQueue queue,
        AgentInputItem item)
    {
        this.parent = parent;
        this.queue = queue;
        this.item = item;
        this.Text = item.Text;
        this.editText = this.Text;
        this.Attachments = this.CreateAttachments(item);
        this.RemoveCommand = new RelayCommand(this.Remove);
        this.EditCommand = new RelayCommand(this.BeginEdit);
        this.SaveEditCommand = new RelayCommand(this.SaveEdit);
        this.CancelEditCommand = new RelayCommand(this.CancelEdit);
    }

    public string Text { get; private set; }

    public ObservableCollection<InputQueueEntryAttachmentViewModel> Attachments { get; }

    public bool HasAttachments => this.Attachments.Count > 0;

    public bool IsEditing
    {
        get => this.isEditing;
        private set
        {
            if (this.SetProperty(ref this.isEditing, value))
            {
                this.RaisePropertyChanged(nameof(this.IsNotEditing));
            }
        }
    }

    public bool IsNotEditing => !this.IsEditing;

    public string EditText
    {
        get => this.editText;
        set => this.SetProperty(ref this.editText, value);
    }

    public ICommand RemoveCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand SaveEditCommand { get; }

    public ICommand CancelEditCommand { get; }

    public void RefreshText(string text)
    {
        this.Text = text;
        if (!this.IsEditing)
        {
            this.EditText = text;
        }
        this.RaisePropertyChanged(nameof(this.Text));
    }

    private void Remove() => this.parent.RemoveQueueItem(this.queue, this.item);

    private void RemoveAttachment(int contentIndex) => this.parent.RemoveQueueItemContent(this.queue, this.item, contentIndex);

    private void BeginEdit()
    {
        this.EditText = this.Text;
        this.IsEditing = true;
        this.EditStarted?.Invoke(this, EventArgs.Empty);
    }

    private void SaveEdit()
    {
        this.parent.UpdateQueueItem(this.queue, this.item, this.EditText);
        this.IsEditing = false;
    }

    private void CancelEdit()
    {
        this.EditText = this.Text;
        this.IsEditing = false;
    }

    private ObservableCollection<InputQueueEntryAttachmentViewModel> CreateAttachments(AgentInputItem item)
    {
        var attachments = new ObservableCollection<InputQueueEntryAttachmentViewModel>();
        for (var contentIndex = 0; contentIndex < item.Contents.Count; contentIndex++)
        {
            if (item.Contents[contentIndex] is not DataContent dataContent)
            {
                continue;
            }

            var attachmentIndex = contentIndex;
            var preview = this.TryCreatePreview(dataContent);
            attachments.Add(new InputQueueEntryAttachmentViewModel(
                preview,
                this.FormatDataContentLabel(dataContent),
                new RelayCommand(() => this.RemoveAttachment(attachmentIndex))));
        }

        return attachments;
    }

    private string FormatDataContentLabel(DataContent dataContent)
    {
        return string.IsNullOrWhiteSpace(dataContent.MediaType) ? "image" : dataContent.MediaType;
    }

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
}
