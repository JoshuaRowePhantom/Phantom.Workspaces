using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Agent.Gui.ViewModels;

internal static class AgentChatFlowDocumentBuilder
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
    };

    public static FlowDocument CreateDocument()
        => new();

    public static Section CreateHistorySection(ChatHistoryItemViewModel item)
    {
        var section = new Section();
        UpdateHistorySection(section, item);
        return section;
    }

    public static void UpdateHistorySection(Section section, ChatHistoryItemViewModel item)
    {
        ClearBlocksSafely(section);
        AppendHistoryItem(section, item, isRunning: false);
    }

    public static Section CreateRunningSection(RunningItemViewModel runningItem)
    {
        var section = new Section();
        UpdateRunningSection(section, runningItem);
        return section;
    }

    public static void UpdateRunningSection(Section section, RunningItemViewModel runningItem)
    {
        ClearBlocksSafely(section);
        foreach (var item in runningItem.HistoryItems)
        {
            AppendHistoryItem(section, item, isRunning: true);
        }
    }

    private static void ClearBlocksSafely(Section section)
    {
        while (section.Blocks.Count > 0)
        {
            section.Blocks.RemoveAt(section.Blocks.Count - 1);
        }
    }

    private static void AppendHistoryItem(
        Section section,
        ChatHistoryItemViewModel item,
        bool isRunning)
    {
        section.Blocks.Add(CreateLabelParagraph(item.RoleLabel, isRunning));

        if (item.HasReasoningLine && !string.IsNullOrWhiteSpace(item.ReasoningDisplayText))
        {
            section.Blocks.Add(CreateReasoningParagraph(item.ReasoningDisplayText));
        }

        foreach (var content in item.Contents)
        {
            AppendContent(section, content);
        }

        if (isRunning)
        {
            section.Blocks.Add(new BlockUIContainer(
                new ProgressBar
                {
                    IsIndeterminate = true,
                    Margin = new Thickness(0, 4, 0, 6),
                    MinHeight = 3,
                }));
        }

        section.Blocks.Add(new Paragraph(new RichRun(string.Empty)));
    }

    private static void AppendContent(
        Section section,
        AIContent content)
    {
        switch (content)
        {
            case TextReasoningContent:
                return;
            case TextContent textContent when !string.IsNullOrWhiteSpace(textContent.Text):
                section.Blocks.Add(new Paragraph(new RichRun(textContent.Text)));
                return;
            case ErrorContent errorContent:
                section.Blocks.Add(CreateErrorParagraph(errorContent.Message));
                return;
            case FunctionCallContent functionCall:
                section.Blocks.Add(CreateMetaParagraph($"tool call: {functionCall.Name}"));
                section.Blocks.Add(CreateMonospaceParagraph(PrettyJson(functionCall.Arguments)));
                return;
            case FunctionResultContent functionResult:
                section.Blocks.Add(CreateMetaParagraph($"tool result: {functionResult.CallId}"));
                section.Blocks.Add(CreateMonospaceParagraph(PrettyJson(functionResult.Result)));
                return;
            case DataContent dataContent:
                AppendDataContent(section, dataContent);
                return;
            case UriContent uriContent:
                section.Blocks.Add(new Paragraph(new RichRun(uriContent.Uri.ToString())));
                return;
            default:
                section.Blocks.Add(new Paragraph(new RichRun(content.ToString() ?? string.Empty)));
                return;
        }
    }

    private static void AppendDataContent(
        Section section,
        DataContent dataContent)
    {
        if (!IsImageMediaType(dataContent.MediaType))
        {
            var mediaLabel = string.IsNullOrWhiteSpace(dataContent.MediaType) ? "[data]" : $"[{dataContent.MediaType}]";
            section.Blocks.Add(new Paragraph(new RichRun(mediaLabel)));
            return;
        }

        var imageLabel = string.IsNullOrWhiteSpace(dataContent.MediaType) ? "image" : dataContent.MediaType;
        var imagePreview = TryCreatePreview(dataContent.Data.ToArray());
        if (imagePreview is null)
        {
            section.Blocks.Add(new Paragraph(new RichRun(imageLabel)));
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
            Stretch = Stretch.Uniform,
        });
        imageContainer.Children.Add(new TextBlock
        {
            Text = imageLabel,
            FontSize = 11,
            Foreground = Brushes.Gray,
        });
        section.Blocks.Add(new BlockUIContainer(imageContainer));
    }

    private static Paragraph CreateLabelParagraph(string label, bool isRunning)
    {
        var paragraph = new Paragraph(new RichRun(isRunning ? $"{label} (running)" : label))
        {
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 6, 0, 2),
        };
        return paragraph;
    }

    private static Paragraph CreateReasoningParagraph(string text)
    {
        var paragraph = new Paragraph(new RichRun(text))
        {
            FontStyle = FontStyle.Italic,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 4),
        };
        return paragraph;
    }

    private static Paragraph CreateMetaParagraph(string text)
    {
        return new Paragraph(new RichRun(text))
        {
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 2),
        };
    }

    private static Paragraph CreateMonospaceParagraph(string text)
    {
        return new Paragraph(new RichRun(text))
        {
            FontFamily = new FontFamily("Cascadia Code,Consolas,Courier New,monospace"),
            Margin = new Thickness(0, 0, 0, 6),
        };
    }

    private static Paragraph CreateErrorParagraph(string? message)
    {
        return new Paragraph(new RichRun(message ?? string.Empty))
        {
            Foreground = new SolidColorBrush(Color.Parse("#FFB3B3")),
            Margin = new Thickness(0, 2, 0, 4),
        };
    }

    private static string PrettyJson(object? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (value is string s)
        {
            return TryPrettyPrintJson(s, out var pretty) ? pretty : s;
        }

        if (value is JsonElement element)
        {
            return JsonSerializer.Serialize(element, PrettyJsonOptions);
        }

        try
        {
            return JsonSerializer.Serialize(value, PrettyJsonOptions);
        }
        catch (NotSupportedException)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static bool TryPrettyPrintJson(string text, out string pretty)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            pretty = JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
            return true;
        }
        catch (JsonException)
        {
            pretty = string.Empty;
            return false;
        }
    }

    private static bool IsImageMediaType(string? mediaType)
        => !string.IsNullOrWhiteSpace(mediaType) && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static Bitmap? TryCreatePreview(byte[] bytes)
    {
        try
        {
            return new Bitmap(new MemoryStream(bytes));
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
