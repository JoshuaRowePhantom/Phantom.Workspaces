using System.Collections.Immutable;
using System.Text.Json;

namespace Phantom.Workspaces.Llm.Provider.Llama;

public static class OllamaStreamEventParser
{
    public static ImmutableArray<LlmStreamEvent> ParseLine(
        string line)
    {
        var normalizedLine = NormalizeLine(line);
        if (string.IsNullOrWhiteSpace(normalizedLine))
        {
            return ImmutableArray<LlmStreamEvent>.Empty;
        }

        using var document = JsonDocument.Parse(normalizedLine);
        var root = document.RootElement;
        var model = GetString(root, "model");
        var createdAt = GetDateTimeOffset(root, "created_at");
        var events = ImmutableArray.CreateBuilder<LlmStreamEvent>();

        if (root.TryGetProperty("response", out var responseElement)
            && responseElement.ValueKind == JsonValueKind.String)
        {
            var response = responseElement.GetString();
            if (!string.IsNullOrEmpty(response))
            {
                events.Add(CreateAssistantTurn(response, model, createdAt));
            }
        }

        if (root.TryGetProperty("message", out var messageElement))
        {
            var messageEvent = ParseMessage(messageElement, model, createdAt);
            if (messageEvent is not null)
            {
                events.Add(messageEvent);
            }
        }

        return events.ToImmutable();
    }

    private static LlmStreamEvent? ParseMessage(
        JsonElement messageElement,
        string? model,
        DateTimeOffset? createdAt)
    {
        var content = GetString(messageElement, "content");
        var thinking = GetString(messageElement, "thinking");
        var toolCalls = ParseToolCalls(messageElement);

        if (string.IsNullOrEmpty(content)
            && string.IsNullOrEmpty(thinking)
            && toolCalls is null)
        {
            return null;
        }

        if (toolCalls is not null
            && string.IsNullOrEmpty(content)
            && string.IsNullOrEmpty(thinking))
        {
            return new LlmStreamEvent
            {
                Event = new LlmEvent
                {
                    StartTime = createdAt ?? DateTimeOffset.UtcNow,
                    EndTime = createdAt ?? DateTimeOffset.UtcNow,
                    Model = model,
                    EventKind = LlmEventKinds.ToolCall,
                    Role = LlmRoles.Assistant,
                    ToolCalls = toolCalls,
                    CorrelationId = toolCalls.Count == 1 ? toolCalls[0].CorrelationId : null,
                },
            };
        }

        return new LlmStreamEvent
        {
            Event = new LlmEvent
            {
                StartTime = createdAt ?? DateTimeOffset.UtcNow,
                EndTime = createdAt ?? DateTimeOffset.UtcNow,
                Model = model,
                EventKind = LlmEventKinds.Turn,
                Role = LlmRoles.Assistant,
                Content = content,
                Thinking = thinking,
                ToolCalls = toolCalls,
            },
        };
    }

    private static ImmutableList<LlmEvent>? ParseToolCalls(
        JsonElement messageElement)
    {
        if (!messageElement.TryGetProperty("tool_calls", out var toolCallsElement)
            || toolCallsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var toolCalls = ImmutableList.CreateBuilder<LlmEvent>();
        foreach (var toolCallElement in toolCallsElement.EnumerateArray())
        {
            var functionElement = toolCallElement.TryGetProperty("function", out var value)
                ? value
                : default;

            var toolName = GetString(functionElement, "name");
            var correlationId = GetString(toolCallElement, "id");
            var arguments = GetRawJson(functionElement, "arguments");

            if (string.IsNullOrEmpty(toolName) && string.IsNullOrEmpty(arguments))
            {
                continue;
            }

            toolCalls.Add(new LlmEvent
            {
                EventKind = LlmEventKinds.ToolCall,
                ToolName = toolName,
                Content = arguments,
                CorrelationId = correlationId,
            });
        }

        return toolCalls.Count > 0 ? toolCalls.ToImmutable() : null;
    }

    private static LlmStreamEvent CreateAssistantTurn(
        string content,
        string? model,
        DateTimeOffset? createdAt)
    {
        return new LlmStreamEvent
        {
            Event = new LlmEvent
            {
                StartTime = createdAt ?? DateTimeOffset.UtcNow,
                EndTime = createdAt ?? DateTimeOffset.UtcNow,
                Model = model,
                EventKind = LlmEventKinds.Turn,
                Role = LlmRoles.Assistant,
                Content = content,
            },
        };
    }

    private static string? GetString(
        JsonElement element,
        string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static string? GetRawJson(
        JsonElement element,
        string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.GetRawText();
    }

    private static DateTimeOffset? GetDateTimeOffset(
        JsonElement element,
        string propertyName)
    {
        var value = GetString(element, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : null;
    }

    private static string NormalizeLine(
        string line)
    {
        return line.StartsWith("< ", StringComparison.Ordinal)
            ? line[2..]
            : line;
    }
}
