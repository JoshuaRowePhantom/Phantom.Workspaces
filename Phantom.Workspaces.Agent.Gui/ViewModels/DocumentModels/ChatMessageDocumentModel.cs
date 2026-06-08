using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

internal sealed class ChatMessageDocumentModel : AgentChatDocumentBlockModel
{
    private sealed class ContentBinding
    {
        public required AIContent Content { get; init; }

        public List<Block> Blocks { get; } = [];

        public bool IsVisible { get; set; }

        public bool IsReasoning => this.Content is TextReasoningContent;
    }

    private readonly Func<bool> isReasoningVisible;
    private readonly Section labelSection = new();
    private readonly Section contentSection = new();
    private readonly List<ContentBinding> contentBindings = [];

    public ChatMessageDocumentModel(AgentChatHistoryItem item, Func<bool> isReasoningVisible)
    {
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        this.Source = item;
        this.isReasoningVisible = isReasoningVisible;
        this.Section = new Section();
        this.Section.Blocks.Add(this.labelSection);
        this.Section.Blocks.Add(this.contentSection);
        this.Render();
    }

    public AgentChatHistoryItem Source { get; private set; }

    public Section Section { get; }

    public override Block Block => this.Section;

    public void Update(AgentChatHistoryItem item)
    {
        this.Source = item;
        this.Render();
    }

    public void UpdateReasoningVisibility()
    {
        this.SyncReasoningVisibility();
    }

    private void Render()
    {
        DocumentBlockUtilities.ClearBlocks(this.labelSection);
        this.labelSection.Blocks.Add(DocumentBlockUtilities.CreateLabelParagraph(
            this.Source.Role.Value.ToLowerInvariant(),
            this.Source.Role.Value));

        DocumentBlockUtilities.ClearBlocks(this.contentSection);
        this.contentBindings.Clear();
        foreach (var content in this.Source.Contents)
        {
            var contentBinding = new ContentBinding
            {
                Content = content,
            };
            this.contentBindings.Add(contentBinding);
            this.RenderContentBinding(contentBinding, this.isReasoningVisible());
            if (contentBinding.IsVisible)
            {
                foreach (var block in contentBinding.Blocks)
                {
                    this.contentSection.Blocks.Add(block);
                }
            }
        }
    }

    private void SyncReasoningVisibility()
    {
        for (var bindingIndex = 0; bindingIndex < this.contentBindings.Count; bindingIndex++)
        {
            var contentBinding = this.contentBindings[bindingIndex];
            if (!contentBinding.IsReasoning)
            {
                continue;
            }

            var expectedVisible = this.isReasoningVisible() && this.ShouldRenderReasoningContent(contentBinding.Content);
            if (expectedVisible == contentBinding.IsVisible)
            {
                continue;
            }

            if (expectedVisible)
            {
                this.RenderContentBinding(contentBinding, includeReasoningContent: true);
                var targetIndex = this.GetTargetContentIndex(bindingIndex);
                for (var blockIndex = 0; blockIndex < contentBinding.Blocks.Count; blockIndex++)
                {
                    this.contentSection.Blocks.Insert(targetIndex + blockIndex, contentBinding.Blocks[blockIndex]);
                }
            }
            else
            {
                foreach (var block in contentBinding.Blocks)
                {
                    this.contentSection.Blocks.Remove(block);
                }
                contentBinding.Blocks.Clear();
                contentBinding.IsVisible = false;
            }
        }
    }

    private int GetTargetContentIndex(int bindingIndex)
    {
        var targetIndex = 0;
        for (var index = 0; index < bindingIndex; index++)
        {
            if (!this.contentBindings[index].IsVisible)
            {
                continue;
            }

            targetIndex += this.contentBindings[index].Blocks.Count;
        }

        return targetIndex;
    }

    private bool ShouldRenderReasoningContent(AIContent content)
    {
        if (content is not TextReasoningContent reasoningContent)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(reasoningContent.Text);
    }

    private void RenderContentBinding(ContentBinding contentBinding, bool includeReasoningContent)
    {
        contentBinding.Blocks.Clear();
        this.AppendContent(contentBinding.Blocks, contentBinding.Content, includeReasoningContent);
        contentBinding.IsVisible = contentBinding.Blocks.Count > 0;
    }

    private void AppendContent(IList<Block> blocks, AIContent content, bool includeReasoningContent)
    {
        switch (content)
        {
            case TextReasoningContent reasoningContent when includeReasoningContent && !string.IsNullOrWhiteSpace(reasoningContent.Text):
                blocks.Add(DocumentBlockUtilities.CreateReasoningParagraph(reasoningContent.Text));
                return;
            case TextReasoningContent:
                return;
            case TextContent textContent when !string.IsNullOrWhiteSpace(textContent.Text):
                blocks.Add(DocumentBlockUtilities.CreateBodyParagraph(textContent.Text));
                return;
            case ErrorContent errorContent:
                blocks.Add(DocumentBlockUtilities.CreateErrorParagraph(errorContent.Message));
                return;
            case FunctionCallContent functionCall:
                blocks.Add(DocumentBlockUtilities.CreateMetaParagraph($"tool call: {functionCall.Name}"));
                blocks.Add(DocumentBlockUtilities.CreateMonospaceParagraph(DocumentBlockUtilities.PrettyJson(functionCall.Arguments)));
                return;
            case FunctionResultContent functionResult:
                blocks.Add(DocumentBlockUtilities.CreateMetaParagraph($"tool result: {functionResult.CallId}"));
                blocks.Add(DocumentBlockUtilities.CreateMonospaceParagraph(DocumentBlockUtilities.PrettyJson(functionResult.Result)));
                return;
            case DataContent dataContent:
                this.AppendDataContent(blocks, dataContent);
                return;
            case UriContent uriContent:
                blocks.Add(DocumentBlockUtilities.CreateBodyParagraph(uriContent.Uri.ToString()));
                return;
            default:
                blocks.Add(DocumentBlockUtilities.CreateBodyParagraph(content.ToString() ?? string.Empty));
                return;
        }
    }

    private void AppendDataContent(IList<Block> blocks, DataContent dataContent)
    {
        if (!DocumentBlockUtilities.IsImageMediaType(dataContent.MediaType))
        {
            var mediaLabel = string.IsNullOrWhiteSpace(dataContent.MediaType) ? "[data]" : $"[{dataContent.MediaType}]";
            blocks.Add(DocumentBlockUtilities.CreateBodyParagraph(mediaLabel));
            return;
        }

        var imageLabel = string.IsNullOrWhiteSpace(dataContent.MediaType) ? "image" : dataContent.MediaType;
        var imagePreview = DocumentBlockUtilities.TryCreatePreview(dataContent.Data.ToArray());
        if (imagePreview is null)
        {
            blocks.Add(DocumentBlockUtilities.CreateBodyParagraph(imageLabel));
            return;
        }

        var imageContainer = new StackPanel
        {
            Spacing = 4,
        };
        imageContainer.Children.Add(new Image
        {
            Source = imagePreview,
            Width = 192,
            MaxHeight = 160,
            Stretch = Avalonia.Media.Stretch.Uniform,
        });
        imageContainer.Children.Add(new TextBlock
        {
            Text = imageLabel,
            FontSize = 11,
            Foreground = Avalonia.Media.Brushes.Gray,
        });
        blocks.Add(new BlockUIContainer(imageContainer));
    }

}
