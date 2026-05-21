using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Tests;

internal sealed class TestChatClient(
    params ChatResponseUpdate[] updates) : IChatClient
{
    private readonly IReadOnlyCollection<ChatResponseUpdate> updates = updates;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var content = string.Empty;
        await foreach (var update in this.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            content += update.Text;
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, content));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in this.updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }
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
}
