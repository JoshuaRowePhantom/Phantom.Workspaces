using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// Production <see cref="IReverseExecutionHandler"/> for the connecting instance (C). It runs the
/// requested agent locally and streams the resulting <see cref="ChatResponseUpdate"/>s back over the
/// reverse channel. This mirrors <c>AgentRespondHandler</c> (the forward host side) but streams, so
/// the server (S) sees incremental updates. See <c>docs/design/reverse-tunnel-trust-execution.md</c>.
/// </summary>
public sealed class LocalReverseExecutionHandler : IReverseExecutionHandler
{
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
}
