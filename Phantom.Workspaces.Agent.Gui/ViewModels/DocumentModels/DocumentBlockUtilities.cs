using System.IO;
using System.Text.Json;
using Avalonia.Controls.Documents;
using Avalonia.Media.Imaging;

namespace Phantom.Workspaces.Agent.Gui.ViewModels.DocumentModels;

internal static class DocumentBlockUtilities
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
    };

    public static void ClearBlocks(Section section)
    {
        while (section.Blocks.Count > 0)
        {
            section.Blocks.RemoveAt(section.Blocks.Count - 1);
        }
    }

    public static Paragraph CreateLabelParagraph(
        string label,
        string role)
    {
        var classes = new[] { "agent-chat-role-label", $"agent-chat-role-label-{role}" };
        return new Paragraph(new RichRun(label))
        {
            Classes = { classes[0], classes[1] },
        };
    }

    public static Paragraph CreateReasoningParagraph(string text)
    {
        return new Paragraph(new RichRun(text))
        {
            Classes = { "agent-chat-reasoning" },
        };
    }

    public static Paragraph CreateBodyParagraph(string text)
    {
        return new Paragraph(new RichRun(text))
        {
            Classes = { "agent-chat-body" },
        };
    }

    public static Paragraph CreateMetaParagraph(string text)
    {
        return new Paragraph(new RichRun(text))
        {
            Classes = { "agent-chat-meta" },
        };
    }

    public static Paragraph CreateMonospaceParagraph(string text)
    {
        return new Paragraph(new RichRun(text))
        {
            Classes = { "agent-chat-monospace" },
        };
    }

    public static Paragraph CreateErrorParagraph(string? message)
    {
        return new Paragraph(new RichRun(message ?? string.Empty))
        {
            Classes = { "agent-chat-error" },
        };
    }

    public static string PrettyJson(object? value)
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

    public static bool IsImageMediaType(string? mediaType)
        => !string.IsNullOrWhiteSpace(mediaType) && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public static Bitmap? TryCreatePreview(byte[] bytes)
    {
        try
        {
            return new Bitmap(new MemoryStream(bytes));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
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
}
