using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class ChatHistoryItemViewModel : ViewModelBase, IDisposable
{
    private IReadOnlyList<AIContent> contents;
    private string text;
    private string reasoningText;
    private bool isInProgress;
    private bool isReasoningVisible;

    public ChatHistoryItemViewModel(AgentChatHistoryItem item)
    {
        this.IsUser = item.Role == ChatRole.User;
        this.RoleLabel = this.IsUser ? "user" : "assistant";
        this.contents = item.Contents;
        this.text = item.Text;
        this.reasoningText = item.ReasoningText;
        this.isInProgress = item.IsInProgress;
        this.Attachments = [];
        this.UpdateAttachments(item.Contents);
    }

    public bool IsUser { get; }
    public string RoleLabel { get; }

    public string Text
    {
        get => this.text;
        private set
        {
            if (this.SetProperty(ref this.text, value))
            {
                this.RaisePropertyChanged(nameof(this.HasText));
            }
        }
    }

    public bool HasText => !string.IsNullOrWhiteSpace(this.text);

    public bool IsInProgress
    {
        get => this.isInProgress;
        private set
        {
            if (!this.SetProperty(ref this.isInProgress, value))
            {
                return;
            }

            this.RaisePropertyChanged(nameof(this.HasReasoningLine));
            this.RaisePropertyChanged(nameof(this.ReasoningDisplayText));
        }
    }

    public string ReasoningText
    {
        get => this.reasoningText;
        private set
        {
            if (this.SetProperty(ref this.reasoningText, value))
            {
                this.RaisePropertyChanged(nameof(this.HasReasoningLine));
                this.RaisePropertyChanged(nameof(this.ReasoningDisplayText));
            }
        }
    }

    public bool HasReasoningLine => !this.IsUser && (this.IsInProgress || (this.isReasoningVisible && !string.IsNullOrEmpty(this.reasoningText)));

    public ObservableCollection<ChatHistoryImageViewModel> Attachments { get; }

    public bool HasAttachments => this.Attachments.Count > 0;

    public IReadOnlyList<AIContent> Contents
    {
        get => this.contents;
        private set => this.SetProperty(ref this.contents, value);
    }

    public string ReasoningDisplayText
        => this.IsUser
            ? string.Empty
            : this.isReasoningVisible && !string.IsNullOrEmpty(this.reasoningText)
                ? this.reasoningText
                : this.IsInProgress
                    ? "Thinking ..."
                    : string.Empty;

    public void UpdateFrom(AgentChatHistoryItem item)
    {
        this.Text = item.Text;
        this.ReasoningText = item.ReasoningText;
        this.IsInProgress = item.IsInProgress;
        this.Contents = item.Contents;
        this.UpdateAttachments(item.Contents);
    }

    public void SetReasoningVisible(bool visible)
    {
        if (!this.SetProperty(ref this.isReasoningVisible, visible))
        {
            return;
        }

        this.RaisePropertyChanged(nameof(this.HasReasoningLine));
        this.RaisePropertyChanged(nameof(this.ReasoningDisplayText));
    }

    public void Dispose()
    {
        foreach (var attachment in this.Attachments)
        {
            attachment.Dispose();
        }
    }

    private void UpdateAttachments(IReadOnlyList<AIContent> contents)
    {
        foreach (var attachment in this.Attachments)
        {
            attachment.Dispose();
        }

        this.Attachments.Clear();

        foreach (var content in contents)
        {
            if (content is not DataContent dataContent || !IsImageMediaType(dataContent.MediaType))
            {
                continue;
            }

            var bytes = dataContent.Data.ToArray();
            var label = this.FormatImageLabel(bytes, dataContent.MediaType);
            this.Attachments.Add(new ChatHistoryImageViewModel(this.TryCreatePreview(bytes), label));
        }

        this.RaisePropertyChanged(nameof(this.HasAttachments));
        this.RaisePropertyChanged(nameof(this.Attachments));
    }

    private static bool IsImageMediaType(string? mediaType)
        => !string.IsNullOrWhiteSpace(mediaType) && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private string FormatImageLabel(byte[] data, string? mediaType)
    {
        try
        {
            using var bitmap = new Bitmap(new MemoryStream(data));
            return string.IsNullOrWhiteSpace(mediaType)
                ? $"image {bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}"
                : $"{mediaType} {bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}";
        }
        catch (InvalidOperationException)
        {
            return string.IsNullOrWhiteSpace(mediaType) ? "image" : mediaType;
        }
        catch (ArgumentException)
        {
            return string.IsNullOrWhiteSpace(mediaType) ? "image" : mediaType;
        }
    }

    private Bitmap? TryCreatePreview(byte[] data)
    {
        try
        {
            return new Bitmap(new MemoryStream(data));
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

}
