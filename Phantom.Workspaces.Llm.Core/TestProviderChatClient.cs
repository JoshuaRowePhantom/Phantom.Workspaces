using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Lightweight deterministic provider intended for tests and local development.
/// </summary>
public sealed class TestProviderChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var responseText = BuildResponse(GetLatestUserText(messages));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var latestUserText = GetLatestUserText(messages);
        if (latestUserText.StartsWith("reasoning-tokens: ", StringComparison.Ordinal))
        {
            var reasoning = latestUserText["reasoning-tokens: ".Length..];
            foreach (var token in reasoning)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(token.ToString())]);
                await Task.Yield();
            }

            yield break;
        }

        if (latestUserText.StartsWith("thinking-tokens: ", StringComparison.Ordinal))
        {
            var reasoning = latestUserText["thinking-tokens: ".Length..];
            foreach (var token in reasoning)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(token.ToString())]);
                await Task.Yield();
            }

            yield break;
        }

        var requestedOutput = BuildResponse(latestUserText);
        yield return new ChatResponseUpdate(ChatRole.Assistant, requestedOutput)
        {
            FinishReason = ChatFinishReason.Stop,
        };
        await Task.Yield();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(IChatClient) ? this : null;

    public void Dispose()
    {
    }

    private static string GetLatestUserText(IEnumerable<ChatMessage> messages)
    {
        var latestUserText = messages
            .Where(static m => m.Role == ChatRole.User)
            .SelectMany(static m => m.Contents)
            .OfType<TextContent>()
            .Select(static c => c.Text)
            .LastOrDefault();

        return latestUserText ?? string.Empty;
    }

    private static string BuildResponse(string latestUserText)
    {
        return string.IsNullOrWhiteSpace(latestUserText)
            ? "test"
            : $"test: {latestUserText}";
    }
}
