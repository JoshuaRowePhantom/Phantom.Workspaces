using System.Text;
using System.Text.Json;

namespace Phantom.Workspaces.Llm.Provider.Llama;

public sealed class OllamaHttpLlmProvider : ILlmProvider
{
    private readonly HttpClient httpClient;
    private readonly OllamaOptions options;

    public OllamaHttpLlmProvider(
        HttpClient httpClient,
        OllamaOptions options)
    {
        this.httpClient = httpClient;
        this.options = options;
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmConversation conversation,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var requestBody = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = this.options.Model,
            ["messages"] = conversation.Events
                .Where(ShouldSendMessage)
                .Select(MapMessage)
                .ToArray(),
            ["stream"] = true,
        };
        var think = MapThinkingLevel(this.options.ThinkingLevel);
        if (think is not null)
        {
            requestBody["think"] = think;
        }

        var modelOptions = BuildModelOptions(this.options);
        if (modelOptions.Count > 0)
        {
            requestBody["options"] = modelOptions;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, this.options.Endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"),
        };

        using var response = await this.httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var streamEvent in new OllamaStreamLlmProvider(responseStream)
                           .StreamAsync(conversation, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            yield return streamEvent;
        }
    }

    private static object MapMessage(
        LlmEvent llmEvent)
    {
        var message = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = llmEvent.Role,
            ["content"] = llmEvent.Content,
        };

        if (llmEvent.ToolCalls is not null && llmEvent.ToolCalls.Count > 0)
        {
            message["tool_calls"] = llmEvent.ToolCalls.Select(MapToolCall).ToArray();
        }

        if (!string.IsNullOrEmpty(llmEvent.ToolName))
        {
            message["tool_name"] = llmEvent.ToolName;
        }

        if (!string.IsNullOrEmpty(llmEvent.CorrelationId))
        {
            message["tool_call_id"] = llmEvent.CorrelationId;
        }

        return message;
    }

    private static bool ShouldSendMessage(
        LlmEvent llmEvent)
    {
        return !(string.Equals(llmEvent.Role, LlmRoles.Assistant, StringComparison.Ordinal)
                 && string.IsNullOrEmpty(llmEvent.Content)
                 && !string.IsNullOrEmpty(llmEvent.Thinking)
                 && (llmEvent.ToolCalls is null || llmEvent.ToolCalls.Count == 0)
                 && string.IsNullOrEmpty(llmEvent.ToolName));
    }

    private static object MapToolCall(
        LlmEvent llmEvent)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = llmEvent.CorrelationId,
            ["function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["index"] = 0,
                ["name"] = llmEvent.ToolName,
                ["arguments"] = ParseArguments(llmEvent.Content),
            },
        };
    }

    private static object? ParseArguments(
        string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(content);
        }
        catch (JsonException)
        {
            return content;
        }
    }

    private static object? MapThinkingLevel(
        string? thinkingLevel)
    {
        if (string.IsNullOrWhiteSpace(thinkingLevel))
        {
            return null;
        }

        if (string.Equals(thinkingLevel, OllamaThinkingLevel.True, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(thinkingLevel, OllamaThinkingLevel.False, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return thinkingLevel;
    }

    private static Dictionary<string, object?> BuildModelOptions(
        OllamaOptions options)
    {
        var modelOptions = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (options.ContextSize is > 0)
        {
            modelOptions["num_ctx"] = options.ContextSize.Value;
        }

        return modelOptions;
    }
}
