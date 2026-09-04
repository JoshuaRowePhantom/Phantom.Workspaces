using System.Text.Json;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Maps GitHub Copilot SDK tool lifecycle events
/// (<see cref="ToolExecutionStartEvent"/> / <see cref="ToolExecutionCompleteEvent"/>) onto the
/// Microsoft.Extensions.AI content types (<see cref="FunctionCallContent"/> /
/// <see cref="FunctionResultContent"/>) that every other provider produces, so the existing chat
/// GUI renders Copilot tool calls and results with no GUI changes.
/// </summary>
/// <remarks>
/// This is a pure, side-effect-free helper so the mapping is unit-testable without the Copilot CLI.
/// </remarks>
public static class CopilotToolEventMapper
{
    /// <summary>
    /// Maps a tool-execution start event to a <see cref="FunctionCallContent"/>, correlating by the
    /// SDK <c>ToolCallId</c> and parsing the (JSON) arguments into a named-argument dictionary.
    /// </summary>
    public static FunctionCallContent MapToolStart(ToolExecutionStartEvent startEvent)
    {
        ArgumentNullException.ThrowIfNull(startEvent);
        var data = startEvent.Data;

        var name = !string.IsNullOrWhiteSpace(data.ToolName)
            ? data.ToolName
            : !string.IsNullOrWhiteSpace(data.McpToolName)
                ? data.McpToolName
                : "tool";

        return new FunctionCallContent(
            data.ToolCallId ?? string.Empty,
            name,
            ParseArguments(data.Arguments));
    }

    /// <summary>
    /// Maps a tool-execution complete event to a <see cref="FunctionResultContent"/>, correlating by
    /// the SDK <c>ToolCallId</c>. A failed call surfaces its error message; a successful call
    /// surfaces the textual/terminal/structured result.
    /// </summary>
    public static FunctionResultContent MapToolComplete(ToolExecutionCompleteEvent completeEvent)
    {
        ArgumentNullException.ThrowIfNull(completeEvent);
        var data = completeEvent.Data;

        var result = data.Success
            ? BuildSuccessResult(data.Result)
            : BuildErrorResult(data.Error);

        return new FunctionResultContent(data.ToolCallId ?? string.Empty, result);
    }

    private static IDictionary<string, object?>? ParseArguments(object? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        if (TryGetJsonElement(arguments, out var element))
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var parsed = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    parsed[property.Name] = property.Value.Clone();
                }

                return parsed;
            }

            // A non-object JSON payload (array or scalar) cannot be a named-argument map; pass it
            // through as a single raw argument so it is still visible to the user.
            return new Dictionary<string, object?> { ["arguments"] = element.Clone() };
        }

        // Unparseable arguments fall back to a single raw-string argument.
        return new Dictionary<string, object?> { ["arguments"] = arguments.ToString() };
    }

    private static bool TryGetJsonElement(object arguments, out JsonElement element)
    {
        switch (arguments)
        {
            case JsonElement jsonElement:
                element = jsonElement;
                return true;

            case string text when !string.IsNullOrWhiteSpace(text):
                try
                {
                    using var document = JsonDocument.Parse(text);
                    element = document.RootElement.Clone();
                    return true;
                }
                catch (JsonException)
                {
                    element = default;
                    return false;
                }

            case string:
                element = default;
                return false;

            default:
                try
                {
                    var json = JsonSerializer.Serialize(arguments);
                    using var document = JsonDocument.Parse(json);
                    element = document.RootElement.Clone();
                    return true;
                }
                catch (JsonException)
                {
                    element = default;
                    return false;
                }
        }
    }

    private static object BuildSuccessResult(ToolExecutionCompleteResult? result)
    {
        if (result is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrEmpty(result.DetailedContent))
        {
            return result.DetailedContent;
        }

        if (!string.IsNullOrEmpty(result.Content))
        {
            return result.Content;
        }

        if (result.Contents is { Length: > 0 })
        {
            foreach (var content in result.Contents)
            {
                switch (content)
                {
                    case ToolExecutionCompleteContentText text when !string.IsNullOrEmpty(text.Text):
                        return text.Text;

                    case ToolExecutionCompleteContentShellExit shellExit:
                        return new TerminalToolResult(shellExit.ExitCode, shellExit.OutputPreview ?? string.Empty);

#pragma warning disable GHCP001 // Terminal content is deprecated but may still be emitted by older runtimes.
                    case ToolExecutionCompleteContentTerminal terminal:
                        return new TerminalToolResult(terminal.ExitCode, terminal.Text ?? string.Empty);
#pragma warning restore GHCP001

                    case ToolExecutionCompleteContentImage image:
                        return new ImageToolResult(image.MimeType ?? string.Empty);
                }
            }
        }

        return string.Empty;
    }

    private static object BuildErrorResult(ToolExecutionCompleteError? error)
    {
        var message = error?.Message;
        return string.IsNullOrWhiteSpace(message) ? "The tool call failed." : message;
    }

    /// <summary>A structured terminal tool result (exit code and output text).</summary>
    public sealed record TerminalToolResult(double? ExitCode, string Text);

    /// <summary>A structured image tool result (its MIME type; binary data is not inlined).</summary>
    public sealed record ImageToolResult(string MimeType);
}
