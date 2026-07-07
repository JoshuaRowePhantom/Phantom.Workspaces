using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// <see cref="IChatClient"/> created for the <c>github-copilot-subagent</c> provider.
/// Implements <see cref="ICopilotSubAgentReceiver"/> so that <c>CopilotSdkChatClient</c>
/// can forward sub-agent events by resolving the receiver via
/// <c>GetService&lt;ICopilotSubAgentReceiver&gt;()</c>.
/// </summary>
internal sealed class CopilotSubAgentChatClient : IChatClient, ICopilotSubAgentReceiver, IHostedAgentChatClient
{
    private readonly Channel<ChatResponseUpdate> _channel =
        Channel.CreateUnbounded<ChatResponseUpdate>();

    /// <inheritdoc/>
    public void Push(ChatResponseUpdate update) =>
        _channel.Writer.TryWrite(update);

    /// <inheritdoc/>
    public void Complete() =>
        _channel.Writer.TryComplete();

    /// <inheritdoc/>
    public void Fail(Exception exception) =>
        _channel.Writer.TryComplete(exception);

    /// <summary>
    /// Reads all updates from the internal channel until <see cref="Complete"/> or
    /// <see cref="Fail"/> is called, or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in _channel.Reader.ReadAllAsync(cancellationToken))
            yield return update;
    }

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Hosted sub-agent chat clients do not accept direct calls.");

    /// <summary>
    /// Returns <c>this</c> when <paramref name="serviceType"/> is <see cref="ICopilotSubAgentReceiver"/>;
    /// otherwise returns <c>null</c>.
    /// </summary>
    public object? GetService(Type serviceType, object? key = null) =>
        serviceType == typeof(ICopilotSubAgentReceiver) ? this : null;

    /// <inheritdoc/>
    public void Dispose() { }
}
