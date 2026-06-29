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
public sealed class LocalReverseExecutionHandler : IReverseExecutionHandler
{
    private readonly LocalTrustedExecutor localExecutor;

    /// <summary>Creates a handler backed by the supplied executor (or a default one if <see langword="null"/>).</summary>
    public LocalReverseExecutionHandler(LocalTrustedExecutor? localExecutor = null)
    {
        this.localExecutor = localExecutor ?? new LocalTrustedExecutor();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
        RemoteAgentRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

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
