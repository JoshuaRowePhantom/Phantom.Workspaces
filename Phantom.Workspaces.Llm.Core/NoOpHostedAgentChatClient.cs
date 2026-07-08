using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// A no-op chat client that implements <see cref="IHostedAgentChatClient"/> so that
/// restored sub-agent <see cref="AgentChat"/> instances have <c>AcceptsUserInput = false</c>
/// and receive no new pushes.
/// </summary>
internal sealed class NoOpHostedAgentChatClient : IChatClient, IHostedAgentChatClient
{
#pragma warning disable CS1998 // Async method lacks 'await' — intentional: returns empty stream
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield break;
    }
#pragma warning restore CS1998

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Restored sub-agent chat clients do not accept new calls.");

    public object? GetService(Type serviceType, object? key = null) => null;

    public void Dispose() { }
}
