using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Echo;

/// <summary>
/// Small deterministic chat client for tests and exploration.
/// Echoes user messages in various formats based on special prefixes.
/// </summary>
public sealed class EchoChatClient : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var responseText = string.Empty;
        await foreach (var update in this.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            responseText += update.Text;
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var requestedOutput = GetRequestedOutput(messages);

        if (requestedOutput.StartsWith("tool_use: ", StringComparison.Ordinal))
        {
            var toolUseText = requestedOutput["tool_use: ".Length..];
            var separatorIndex = toolUseText.IndexOf(' ');
            var toolName = separatorIndex >= 0 ? toolUseText[..separatorIndex] : toolUseText;
            var arguments = separatorIndex >= 0 ? toolUseText[(separatorIndex + 1)..] : string.Empty;

            yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent($"Calling tool {toolName}")]);
            yield return new ChatResponseUpdate(ChatRole.Assistant, $"[tool: {toolName} {arguments}]");
            yield break;
        }

        if (requestedOutput.StartsWith("content-tokens: ", StringComparison.Ordinal))
        {
            var content = requestedOutput["content-tokens: ".Length..];
            foreach (var token in content)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, token.ToString());
                await Task.Yield();
            }

            yield break;
        }

        if (requestedOutput.StartsWith("thinking-tokens: ", StringComparison.Ordinal))
        {
            var thinking = requestedOutput["thinking-tokens: ".Length..];
            foreach (var token in thinking)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(token.ToString())]);
                await Task.Yield();
            }

            yield break;
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, requestedOutput);
    }

    public object? GetService(
        Type serviceType,
        object? serviceKey = null)
    {
        return serviceType == typeof(IChatClient) ? this : null;
    }

    public void Dispose()
    {
    }

    private static string GetRequestedOutput(IEnumerable<ChatMessage> messages)
    {
        var lastUserMessage = messages
            .Where(m => m.Role == ChatRole.User)
            .LastOrDefault();

        return lastUserMessage?.Text ?? string.Empty;
    }
}
