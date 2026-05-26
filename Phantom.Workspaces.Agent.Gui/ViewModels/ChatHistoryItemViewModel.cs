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
    private IReadOnlyList<AIContent> renderableContents;
    private string text;
    private string reasoningText;
    private bool isInProgress;
    private bool isReasoningVisible;

    public ChatHistoryItemViewModel(AgentChatHistoryItem item)
    {
        this.Role = item.Role;
        this.IsUser = item.Role == ChatRole.User;
        this.RoleLabel = item.Role.Value.ToLowerInvariant();
        this.contents = item.Contents;
        this.renderableContents = CreateRenderableContents(item.Contents);
        this.text = item.Text;
        this.reasoningText = item.ReasoningText;
        this.isInProgress = item.IsInProgress;
        this.Attachments = [];
        this.UpdateAttachments(item.Contents);
    }

    public ChatRole Role { get; }
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

    public IReadOnlyList<AIContent> RenderableContents
    {
        get => this.renderableContents;
        private set => this.SetProperty(ref this.renderableContents, value);
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
        if (!AreContentsEquivalent(this.contents, item.Contents))
        {
            this.Contents = item.Contents;
            this.RenderableContents = CreateRenderableContents(item.Contents);
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

    private static IReadOnlyList<AIContent> CreateRenderableContents(IReadOnlyList<AIContent> contents)
    {
        if (contents.Count == 0)
        {
            return Array.Empty<AIContent>();
        }

        var filtered = new List<AIContent>(contents.Count);
        foreach (var content in contents)
        {
            if (content is TextReasoningContent)
            {
                continue;
            }

            if (content is DataContent data && IsImageMediaType(data.MediaType))
            {
                continue;
            }

            filtered.Add(content);
        }

        return filtered.Count == 0 ? Array.Empty<AIContent>() : filtered;
    }

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

        for (var i = 0; i < left.Count; i++)
        {
            if (!AreContentItemsEquivalent(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreContentItemsEquivalent(AIContent left, AIContent right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.GetType() != right.GetType())
        {
            return false;
        }

        return (left, right) switch
        {
            (TextContent l, TextContent r) => string.Equals(l.Text, r.Text, StringComparison.Ordinal),
            (TextReasoningContent l, TextReasoningContent r) => string.Equals(l.Text, r.Text, StringComparison.Ordinal),
            (FunctionCallContent l, FunctionCallContent r) =>
                string.Equals(l.Name, r.Name, StringComparison.Ordinal) &&
                string.Equals(l.CallId, r.CallId, StringComparison.Ordinal) &&
                string.Equals(NormalizeObject(l.Arguments), NormalizeObject(r.Arguments), StringComparison.Ordinal),
            (FunctionResultContent l, FunctionResultContent r) =>
                string.Equals(l.CallId, r.CallId, StringComparison.Ordinal) &&
                string.Equals(NormalizeObject(l.Result), NormalizeObject(r.Result), StringComparison.Ordinal),
            (UriContent l, UriContent r) =>
                Equals(l.Uri, r.Uri) &&
                string.Equals(l.MediaType, r.MediaType, StringComparison.Ordinal),
            (DataContent l, DataContent r) =>
                string.Equals(l.MediaType, r.MediaType, StringComparison.Ordinal) &&
                l.Data.Span.SequenceEqual(r.Data.Span),
            _ => string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal),
        };
    }

    private static string NormalizeObject(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is JsonElement element)
        {
            return element.GetRawText();
        }

        if (value is string s)
        {
            return s;
        }

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (NotSupportedException)
        {
            return value.ToString() ?? string.Empty;
        }
    }
}
