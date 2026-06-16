using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// An <see cref="IChatClient"/> that relays a conversation to a connected instance over the reverse
/// channel (a <see cref="IReverseConnection"/> in the <see cref="ReverseExecutionRegistry"/>), which
/// runs the agent under its own trust profile and streams the result back. This is the reverse-tunnel
/// counterpart to <c>WebRemoteChatClient</c>: the transport is inverted (the connected instance is
/// reached over its existing duplex connection rather than by dialing it).
/// </summary>
public sealed class ReverseRemoteChatClient : IChatClient
{
    private readonly ReverseExecutionRegistry registry;
    private readonly string targetClientInstance;
    private readonly string agentDefinitionJson;
    private readonly string? agentSessionId;

    public ReverseRemoteChatClient(
        ReverseExecutionRegistry registry,
        string targetClientInstance,
        string agentDefinitionJson,
        string? agentSessionId = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentException.ThrowIfNullOrWhiteSpace(targetClientInstance);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentDefinitionJson);
        this.targetClientInstance = targetClientInstance;
        this.agentDefinitionJson = agentDefinitionJson;
        this.agentSessionId = agentSessionId;
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in this.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            updates.Add(update);
        }

        return updates.ToChatResponse();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (!this.registry.TryGetConnection(this.targetClientInstance, out var connection))
        {
            throw new InvalidOperationException(
                $"No reverse connection is available for client instance '{this.targetClientInstance}'.");
        }

        var request = new RemoteAgentRequest
        {
            AgentDefinitionJson = this.agentDefinitionJson,
            AgentSessionId = this.agentSessionId,
            Messages = messages.ToArray(),
        };

        await foreach (var update in connection.ExecuteAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
