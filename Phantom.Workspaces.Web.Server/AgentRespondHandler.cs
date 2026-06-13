using AgentSchema;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Web.Server;

/// <summary>
/// Executes a remote agent turn for the <c>POST /agent/respond</c> endpoint. This is the remote
/// host side of the Workspaces trust-model remoting: it runs the agent locally (model and, in
/// future, trust-scoped tools) and returns the response.
/// </summary>
public static class AgentRespondHandler
{
    /// <summary>
    /// Runs a single agent turn for the supplied request and returns the chat response.
    /// </summary>
    public static async Task<ChatResponse> RespondAsync(
        RemoteAgentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agentDefinition = AgentDefinition.FromJson(request.AgentDefinitionJson);
        var (chatClient, _) = AgentFactory.CreateChatClient(agentDefinition);

        var chatOptions = new ChatOptions();
        AgentFactory.ConfigureChatOptions(agentDefinition, chatOptions);

        return await chatClient
            .GetResponseAsync(request.Messages, chatOptions, cancellationToken)
            .ConfigureAwait(false);
    }
}
