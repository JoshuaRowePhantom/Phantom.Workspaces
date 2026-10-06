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
internal sealed class CopilotSubAgentChatClient : IChatClient, ICopilotSubAgentReceiver, IHostedAgentChatClient, ISelfInvokingToolChatClient
{
    private Channel<ChatResponseUpdate> _channel =
        Channel.CreateUnbounded<ChatResponseUpdate>();
    private readonly object channelGate = new();
    private bool completed;

    /// <inheritdoc/>
    public void Push(ChatResponseUpdate update)
    {
        lock (this.channelGate) this._channel.Writer.TryWrite(update);
    }

    /// <inheritdoc/>
    public void Complete()
    {
        lock (this.channelGate)
        {
            this.completed = true;
            this._channel.Writer.TryComplete();
        }
    }

    /// <inheritdoc/>
    public void Fail(Exception exception)
    {
        lock (this.channelGate)
        {
            this.completed = true;
            this._channel.Writer.TryComplete(exception);
        }
    }

    internal bool BeginInvocation()
    {
        lock (this.channelGate)
        {
            if (this.completed)
            {
                this._channel = Channel.CreateUnbounded<ChatResponseUpdate>();
                this.completed = false;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Reads all updates from the internal channel until <see cref="Complete"/> or
    /// <see cref="Fail"/> is called, or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Channel<ChatResponseUpdate> channel;
        lock (this.channelGate) channel = this._channel;
        await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken))
            yield return update;
    }

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Hosted sub-agent chat clients do not accept direct calls.");

    /// <summary>
    /// Returns <c>this</c> when <paramref name="serviceType"/> is <see cref="ICopilotSubAgentReceiver"/>
    /// or <see cref="ISelfInvokingToolChatClient"/> (so the marker survives
    /// <see cref="DelegatingChatClient"/> propagation and <c>ResolveUseProvidedChatClientAsIs</c>
    /// selects the as-is path); otherwise returns <c>null</c>.
    /// </summary>
    public object? GetService(Type serviceType, object? key = null)
    {
        if (serviceType == typeof(ICopilotSubAgentReceiver)) return this;
        if (serviceType == typeof(ISelfInvokingToolChatClient)) return this;
        return null;
    }

    /// <inheritdoc/>
    public void Dispose() { }
}
