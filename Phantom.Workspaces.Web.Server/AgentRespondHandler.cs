using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Trust;
using System.Text.Json;

namespace Phantom.Workspaces.Web.Server;

/// <summary>
/// Executes a remote agent turn for the <c>POST /agent/respond</c> endpoint. This is the remote
/// host side of the Workspaces trust-model remoting: it runs the agent locally and, when the caller
/// supplies a trust profile, enforces its tool-call policy on the agent's tools.
/// </summary>
public static class AgentRespondHandler
{
    /// <summary>
    /// Runs a single agent turn for the supplied request and returns the chat response. When the
    /// request targets a non-local client instance that is currently connected to this host over a
    /// reverse tunnel, the turn is relayed to that connected instance; otherwise the agent runs
    /// locally and (when supplied) the caller's trust profile tool-call policy is enforced.
    /// </summary>
    public static async Task<ChatResponse> RespondAsync(
        RemoteAgentRequest request,
        ReverseExecutionRegistry? reverseExecutionRegistry = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (TryGetConnectedReverseTarget(request, reverseExecutionRegistry, out var targetClientInstance))
        {
            var reverseClient = new ReverseRemoteChatClient(
                reverseExecutionRegistry!,
                targetClientInstance,
                request.AgentDefinitionJson,
                request.AgentSessionId);

            return await reverseClient
                .GetResponseAsync(request.Messages, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        var agentDefinition = AgentDefinition.FromJson(request.AgentDefinitionJson);
        var (chatClient, _) = AgentFactory.CreateChatClient(agentDefinition);

        var chatOptions = new ChatOptions();
        AgentFactory.ConfigureChatOptions(agentDefinition, chatOptions);

        // Enforce the caller-supplied trust profile's tool-call policy on the agent's tools so that
        // disallowed tool calls are denied during this remote execution.
        if (!string.IsNullOrWhiteSpace(request.TrustProfileJson))
        {
            var trustProfile = JsonSerializer.Deserialize<TrustProfile>(request.TrustProfileJson)
                ?? throw new InvalidOperationException("The supplied trust profile content is invalid.");
            TrustToolAuthorization.Apply(chatOptions, trustProfile);
        }

        return await chatClient
            .GetResponseAsync(request.Messages, chatOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TryGetConnectedReverseTarget(
        RemoteAgentRequest request,
        ReverseExecutionRegistry? reverseExecutionRegistry,
        out string targetClientInstance)
    {
        targetClientInstance = request.TargetClientInstance ?? string.Empty;
        return reverseExecutionRegistry is not null
            && !string.IsNullOrWhiteSpace(targetClientInstance)
            && !string.Equals(targetClientInstance, TrustProfile.LocalClientInstance, StringComparison.Ordinal)
            && reverseExecutionRegistry.IsConnected(targetClientInstance);
    }
}
