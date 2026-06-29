using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Shell;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Production <see cref="IReverseExecutionHandler"/> for the connecting instance (C). It runs the
/// requested agent locally and streams the resulting <see cref="ChatResponseUpdate"/>s back over the
/// reverse channel. This mirrors <c>AgentRespondHandler</c> (the forward host side) but streams, so
/// the server (S) sees incremental updates. See <c>docs/design/reverse-tunnel-trust-execution.md</c>.
/// </summary>
/// <remarks>
/// When an <see cref="AgentChatSessionCache"/> is provided and the incoming
/// <see cref="RemoteAgentRequest.AgentSessionId"/> is non-empty, each turn is routed through the
/// cache so stateful providers (e.g. <c>CopilotSdkChatClient</c>) keep their session alive across
/// turns. Without a cache the handler falls back to the existing stateless path.
/// </remarks>
public sealed class LocalReverseExecutionHandler : IReverseExecutionHandler
{
    private readonly LocalTrustedExecutor localExecutor;
    private readonly AgentChatSessionCache? sessionCache;

    /// <summary>
    /// Creates a handler with an optional session cache and executor.
    /// </summary>
    /// <param name="localExecutor">Stream handler executor; a default one is used when <see langword="null"/>.</param>
    /// <param name="sessionCache">
    /// Optional cache for stateful sessions. When provided, requests that carry a non-empty
    /// <see cref="RemoteAgentRequest.AgentSessionId"/> are routed through the cache.
    /// </param>
    public LocalReverseExecutionHandler(
        LocalTrustedExecutor? localExecutor = null,
        AgentChatSessionCache? sessionCache = null)
    {
        this.localExecutor = localExecutor ?? new LocalTrustedExecutor();
        this.sessionCache = sessionCache;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
        RemoteAgentRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrEmpty(request.AgentSessionId) && this.sessionCache is not null)
        {
            var turnRequest = new AgentChatTurnRequest
            {
                AgentDefinitionJson = request.AgentDefinitionJson,
                AgentSessionId = request.AgentSessionId,
                Messages = request.Messages,
            };
            await foreach (var update in this.sessionCache
                .RunTurnAsync(turnRequest, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return update;
            }

            yield break;
        }

        var agentDefinition = AgentDefinition.FromJson(request.AgentDefinitionJson);
        var (chatClient, _) = AgentFactory.CreateChatClient(agentDefinition);

        var chatOptions = new ChatOptions();
        AgentFactory.ConfigureChatOptions(agentDefinition, chatOptions);

        await foreach (var update in chatClient
            .GetStreamingResponseAsync(request.Messages, chatOptions, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <inheritdoc />
    public Task HandleStreamAsync(
        string streamKind,
        string openPayloadJson,
        IStreamMessageChannel channel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamKind);
        ArgumentNullException.ThrowIfNull(openPayloadJson);
        ArgumentNullException.ThrowIfNull(channel);

        var openPayload = JsonDocument.Parse(openPayloadJson).RootElement;
        return this.localExecutor.HandleStreamAsync(streamKind, openPayload, channel, cancellationToken);
    }

    /// <inheritdoc />
    public Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return this.localExecutor.RunToolAsync(
            request with { TargetClientInstance = TrustProfile.LocalClientInstance },
            cancellationToken);
    }
}
