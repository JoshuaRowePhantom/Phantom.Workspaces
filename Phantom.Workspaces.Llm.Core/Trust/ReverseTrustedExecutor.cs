using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// An <see cref="ITrustedExecutor"/> that runs an agent on a connected instance over the reverse
/// tunnel: it builds a thin local agent shell whose chat client (<see cref="ReverseRemoteChatClient"/>)
/// relays the conversation to that instance, which performs the trusted execution under its own trust
/// profile. This is the reverse-direction counterpart to the forward <c>RemoteTrustedExecutor</c>.
/// </summary>
public sealed class ReverseTrustedExecutor : ITrustedExecutor
{
    private readonly ReverseExecutionRegistry registry;

    public ReverseTrustedExecutor(ReverseExecutionRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc />
    public bool CanExecute(string targetClientInstance)
    {
        ArgumentNullException.ThrowIfNull(targetClientInstance);

        // The reverse channel serves remote instances only (never the local '.'), and only while the
        // instance is currently connected.
        return !string.Equals(targetClientInstance, TrustProfile.LocalClientInstance, StringComparison.Ordinal)
            && this.registry.IsConnected(targetClientInstance);
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
                $"No reverse connection is available to execute on client instance '{request.TargetClientInstance}'.");
        }

        var reverseChatClient = new ReverseRemoteChatClient(
            this.registry,
            request.TargetClientInstance,
            request.AgentDefinition.ToJson(),
            request.AgentSessionId);

        var baseServices = request.AgentServices ?? new AgentServices();
        var services = baseServices with { ChatClientOverride = reverseChatClient };

        return AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = request.AgentDefinition,
            AgentSessionId = request.AgentSessionId,
            AgentServices = services,
        });
    }

    /// <inheritdoc />
    public Task<Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(
               "OpenStreamAsync over the reverse tunnel is not yet implemented.");
}
