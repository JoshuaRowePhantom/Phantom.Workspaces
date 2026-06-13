using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Trust;

/// <summary>
/// The Workspaces remoting <see cref="ITrustedExecutor"/>. It runs an agent on a remote
/// Phantom.Workspaces host (identified by a client instance id) by building a thin local agent
/// shell whose chat client relays the conversation to the remote host over HTTP.
/// </summary>
/// <remarks>
/// This is the application-layer counterpart to the Llm.Core <c>LocalTrustedExecutor</c>: trusted
/// execution (containers, processes, tool permissions) happens on the remote host, while this
/// executor owns the cross-machine transport.
/// </remarks>
public sealed class RemoteTrustedExecutor : ITrustedExecutor
{
    private readonly string clientInstance;
    private readonly string endpoint;
    private readonly string? devTunnelAccessToken;

    /// <summary>
    /// Creates a remote executor for a specific client instance and its host endpoint.
    /// </summary>
    /// <param name="clientInstance">The remote client instance id this executor serves.</param>
    /// <param name="endpoint">Absolute base URL of the remote Phantom.Workspaces host.</param>
    /// <param name="devTunnelAccessToken">Optional dev tunnel access token for non-interactive access.</param>
    public RemoteTrustedExecutor(string clientInstance, string endpoint, string? devTunnelAccessToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientInstance);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        if (string.Equals(clientInstance, TrustProfile.LocalClientInstance, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "RemoteTrustedExecutor cannot serve the local client instance '.'.",
                nameof(clientInstance));
        }

        this.clientInstance = clientInstance;
        this.endpoint = endpoint;
        this.devTunnelAccessToken = devTunnelAccessToken;
    }

    /// <summary>The remote client instance id this executor serves.</summary>
    public string ClientInstance => this.clientInstance;

    /// <inheritdoc />
    public bool CanExecute(string targetClientInstance)
    {
        ArgumentNullException.ThrowIfNull(targetClientInstance);
        return string.Equals(targetClientInstance, this.clientInstance, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public Task<AgentChat> CreateAgentChatAsync(
        TrustedExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!this.CanExecute(request.TargetClientInstance))
        {
            throw new InvalidOperationException(
                $"RemoteTrustedExecutor for '{this.clientInstance}' cannot execute on client instance "
                + $"'{request.TargetClientInstance}'.");
        }

        var remoteChatClient = new WebRemoteChatClient(
            this.endpoint,
            request.AgentDefinition.ToJson(),
            request.AgentSessionId,
            this.devTunnelAccessToken);

        var baseServices = request.AgentServices ?? new AgentServices();
        var services = baseServices with { ChatClientOverride = remoteChatClient };

        return AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = request.AgentDefinition,
            AgentSessionId = request.AgentSessionId,
            AgentServices = services,
        });
    }
}
