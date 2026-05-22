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
        var responseText = this.BuildResponse(messages);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new ChatResponseUpdate(ChatRole.Assistant, this.BuildResponse(messages))
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

    private string BuildResponse(IEnumerable<ChatMessage> messages)
    {
        var latestUserText = messages
            .Where(static m => m.Role == ChatRole.User)
            .SelectMany(static m => m.Contents)
            .OfType<TextContent>()
            .Select(static c => c.Text)
            .LastOrDefault();

        return string.IsNullOrWhiteSpace(latestUserText)
            ? "test"
            : $"test: {latestUserText}";
    }
}
