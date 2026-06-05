using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

public sealed class ChatHistoryItemViewModel : ViewModelBase, IDisposable
{
    private IReadOnlyList<AIContent> contents;
    private bool isInProgress;
    private bool isReasoningVisible;

    public ChatHistoryItemViewModel(AgentChatHistoryItem item, bool isInProgress = false)
    {
        this.Role = item.Role;
        this.IsUser = item.Role == ChatRole.User;
        this.RoleLabel = item.Role.Value.ToLowerInvariant();
        this.contents = item.Contents;
        this.isInProgress = isInProgress;
        this.Attachments = [];
        this.UpdateAttachments(item.Contents);
    }

    public ChatRole Role { get; }

    public bool IsUser { get; }

    public string RoleLabel { get; }

    public string Text
        => string.Concat(this.contents.OfType<TextContent>().Select(static content => content.Text));

    public bool HasText => !string.IsNullOrWhiteSpace(this.Text);

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
        => string.Concat(this.contents.OfType<TextReasoningContent>().Select(static content => content.Text));

    public bool HasReasoningLine
        => !this.IsUser
            && (this.ShouldShowThinkingIndicator() || (this.isReasoningVisible && !string.IsNullOrEmpty(this.ReasoningText)));

    public ObservableCollection<ChatHistoryImageViewModel> Attachments { get; }

    public bool HasAttachments => this.Attachments.Count > 0;

    public IReadOnlyList<AIContent> Contents
    {
        get => this.contents;
        private set
        {
            if (!this.SetProperty(ref this.contents, value))
            {
                return;
            }

            this.RaisePropertyChanged(nameof(this.Text));
            this.RaisePropertyChanged(nameof(this.HasText));
            this.RaisePropertyChanged(nameof(this.ReasoningText));
            this.RaisePropertyChanged(nameof(this.RenderableContents));
            this.RaisePropertyChanged(nameof(this.HasReasoningLine));
            this.RaisePropertyChanged(nameof(this.ReasoningDisplayText));
        }
    }

    public IReadOnlyList<AIContent> RenderableContents => this.contents;

    public string ReasoningDisplayText
        => this.IsUser
            ? string.Empty
            : this.isReasoningVisible && !string.IsNullOrEmpty(this.ReasoningText)
                ? this.ReasoningText
                : this.ShouldShowThinkingIndicator()
                    ? "Thinking ..."
                    : string.Empty;

    private bool ShouldShowThinkingIndicator()
        => this.IsInProgress && !this.HasText;

    public void UpdateFrom(AgentChatHistoryItem item)
    {
        if (!AreContentsEquivalent(this.contents, item.Contents))
        {
            this.Contents = item.Contents;
            this.UpdateAttachments(item.Contents);
        }
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

    private static bool AreContentsEquivalent(IReadOnlyList<AIContent> left, IReadOnlyList<AIContent> right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(left[index].ToString(), right[index].ToString(), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
