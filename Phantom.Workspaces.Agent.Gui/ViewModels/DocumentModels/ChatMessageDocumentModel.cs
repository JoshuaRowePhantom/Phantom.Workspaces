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

        public required string Key { get; init; }

        public List<Block> Blocks { get; } = [];

        public bool IsVisible { get; set; }

        public bool IsReasoning => this.Content is TextReasoningContent;
    }

    private readonly Func<bool> isReasoningVisible;
    private readonly Section labelSection = new();
    private readonly Section contentSection = new();
    private readonly List<ContentBinding> contentBindings = [];
    private bool hasRendered;
    private bool lastReasoningVisible;

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

    public void Refresh()
    {
        this.Render();
    }

    private void Render()
    {
        var includeReasoning = this.isReasoningVisible();
        var contents = this.Source.Contents;

        // When reasoning visibility toggles, every binding's visibility may change, so nothing can be
        // reused from the previous render.
        var reasoningChanged = !this.hasRendered || includeReasoning != this.lastReasoningVisible;

        var newBindings = new List<ContentBinding>(contents.Count);
        var anyChange = reasoningChanged || contents.Count != this.contentBindings.Count;
        for (var index = 0; index < contents.Count; index++)
        {
            var content = contents[index];
            var key = ComputeContentKey(content);

            // Reuse the already-rendered blocks when the content at this position is unchanged, so a
            // streaming update (which rebuilds identical leading content) does not re-parse JSON or
            // recreate flow-document blocks. This keeps re-rendering proportional to what changed.
            if (!reasoningChanged
                && index < this.contentBindings.Count
                && this.contentBindings[index].Key == key)
            {
                newBindings.Add(this.contentBindings[index]);
                continue;
            }

            var binding = new ContentBinding
            {
                Content = content,
                Key = key,
            };
            this.RenderContentBinding(binding, includeReasoning);
            newBindings.Add(binding);
            anyChange = true;
        }

        this.hasRendered = true;
        this.lastReasoningVisible = includeReasoning;

        if (!anyChange)
        {
            return;
        }

        this.contentBindings.Clear();
        this.contentBindings.AddRange(newBindings);

        using (this.labelSection.TextDocument?.BeginChange())
        {
            DocumentBlockUtilities.ClearBlocks(this.labelSection);
            this.labelSection.Blocks.Add(DocumentBlockUtilities.CreateLabelParagraph(
                this.Source.Role.Value.ToLowerInvariant(),
                this.Source.Role.Value));

            DocumentBlockUtilities.ClearBlocks(this.contentSection);
            foreach (var contentBinding in this.contentBindings)
            {
                if (!contentBinding.IsVisible)
                {
                    continue;
                }

                foreach (var block in contentBinding.Blocks)
                {
                    this.contentSection.Blocks.Add(block);
                }
            }
        }
    }

    // A cheap, value-based identity for a content block. It excludes large payloads (function-call
    // arguments, tool results, binary data) so comparing it every streaming update stays inexpensive;
    // such payloads are immutable once their content first appears, so the position+identity is enough
    // to know whether a previously rendered block can be reused.
    private static string ComputeContentKey(AIContent content)
    {
        return content switch
        {
            TextReasoningContent reasoning => "reasoning:" + reasoning.Text,
            TextContent text => "text:" + text.Text,
            ErrorContent error => "error:" + error.Message,
            FunctionCallContent call => $"call:{call.CallId}\u0001{call.Name}\u0001{call.Arguments?.Count ?? -1}",
            FunctionResultContent result => "result:" + result.CallId,
            DataContent data => $"data:{data.MediaType}\u0001{data.Data.Length}",
            UriContent uri => "uri:" + uri.Uri,
            _ => $"other:{content.GetType().FullName}\u0001{content}",
        };
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
