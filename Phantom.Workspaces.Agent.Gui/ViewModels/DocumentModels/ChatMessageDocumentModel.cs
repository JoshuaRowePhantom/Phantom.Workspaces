using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

internal sealed class ChatMessageDocumentModel : AgentChatDocumentBlockModel
{
    private readonly Func<bool> isReasoningVisible;
    private readonly bool isRunning;

    public ChatMessageDocumentModel(AgentChatHistoryItem item, bool isRunning, Func<bool> isReasoningVisible)
    {
        ArgumentNullException.ThrowIfNull(isReasoningVisible);
        this.Source = item;
        this.isRunning = isRunning;
        this.isReasoningVisible = isReasoningVisible;
        this.Section = new Section();
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
        // Only update the reasoning section without re-rendering the entire message
        var reasoningText = this.GetReasoningText(this.Source.Contents);
        var reasoningSection = (Section)this.Section.Blocks[1];
        
        DocumentBlockUtilities.ClearBlocksSafely(reasoningSection);
        
        if (this.isReasoningVisible() && !string.IsNullOrWhiteSpace(reasoningText))
        {
            reasoningSection.Blocks.Add(DocumentBlockUtilities.CreateReasoningParagraph(reasoningText));
        }
    }

    private void Render()
    {
        DocumentBlockUtilities.ClearBlocksSafely(this.Section);

        var labelSection = new Section();
        labelSection.Blocks.Add(DocumentBlockUtilities.CreateLabelParagraph(
            this.Source.Role.Value.ToLowerInvariant(),
            this.Source.Role.Value));
        this.Section.Blocks.Add(labelSection);

        var reasoningSection = new Section();
        var reasoningText = this.GetReasoningText(this.Source.Contents);
        if (this.isReasoningVisible() && !string.IsNullOrWhiteSpace(reasoningText))
        {
            reasoningSection.Blocks.Add(DocumentBlockUtilities.CreateReasoningParagraph(reasoningText));
        }

        this.Section.Blocks.Add(reasoningSection);

        var contentSection = new Section();
        foreach (var content in this.Source.Contents)
        {
            if (content is TextReasoningContent)
            {
                continue;
            }

            this.AppendContent(contentSection, content);
        }

        this.Section.Blocks.Add(contentSection);

        var progressSection = new Section();
        if (this.isRunning)
        {
            progressSection.Blocks.Add(new BlockUIContainer(
                new ProgressBar
                {
                    IsIndeterminate = true,
                    Margin = new Thickness(12, 2, 0, 0),
                    MinHeight = 2,
                }));
        }

        this.Section.Blocks.Add(progressSection);
    }

    private void AppendContent(Section section, AIContent content)
    {
        switch (content)
        {
            case TextContent textContent when !string.IsNullOrWhiteSpace(textContent.Text):
                section.Blocks.Add(DocumentBlockUtilities.CreateBodyParagraph(textContent.Text));
                return;
            case ErrorContent errorContent:
                section.Blocks.Add(DocumentBlockUtilities.CreateErrorParagraph(errorContent.Message));
                return;
            case FunctionCallContent functionCall:
                section.Blocks.Add(DocumentBlockUtilities.CreateMetaParagraph($"tool call: {functionCall.Name}"));
                section.Blocks.Add(DocumentBlockUtilities.CreateMonospaceParagraph(DocumentBlockUtilities.PrettyJson(functionCall.Arguments)));
                return;
            case FunctionResultContent functionResult:
                section.Blocks.Add(DocumentBlockUtilities.CreateMetaParagraph($"tool result: {functionResult.CallId}"));
                section.Blocks.Add(DocumentBlockUtilities.CreateMonospaceParagraph(DocumentBlockUtilities.PrettyJson(functionResult.Result)));
                return;
            case DataContent dataContent:
                this.AppendDataContent(section, dataContent);
                return;
            case UriContent uriContent:
                section.Blocks.Add(DocumentBlockUtilities.CreateBodyParagraph(uriContent.Uri.ToString()));
                return;
            default:
                section.Blocks.Add(DocumentBlockUtilities.CreateBodyParagraph(content.ToString() ?? string.Empty));
                return;
        }
    }

    private void AppendDataContent(Section section, DataContent dataContent)
    {
        if (!DocumentBlockUtilities.IsImageMediaType(dataContent.MediaType))
        {
            var mediaLabel = string.IsNullOrWhiteSpace(dataContent.MediaType) ? "[data]" : $"[{dataContent.MediaType}]";
            section.Blocks.Add(DocumentBlockUtilities.CreateBodyParagraph(mediaLabel));
            return;
        }

        var imageLabel = string.IsNullOrWhiteSpace(dataContent.MediaType) ? "image" : dataContent.MediaType;
        var imagePreview = DocumentBlockUtilities.TryCreatePreview(dataContent.Data.ToArray());
        if (imagePreview is null)
        {
            section.Blocks.Add(DocumentBlockUtilities.CreateBodyParagraph(imageLabel));
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
        section.Blocks.Add(new BlockUIContainer(imageContainer));
    }

    private string GetReasoningText(IReadOnlyList<AIContent> contents)
        => string.Concat(contents.OfType<TextReasoningContent>().Select(static content => content.Text));
}
