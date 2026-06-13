namespace Phantom.Workspaces.Llm.Trust;

/// <summary>
/// The local (in-process) <see cref="ITrustedExecutor"/>. This is the Llm.Core execution layer
/// responsible for containers, processes, and tool permissions when running on the local client
/// instance (<c>"."</c>).
/// </summary>
/// <remarks>
/// Agent construction is delegated to <see cref="AgentFactory.CreateAgentChatAsync"/>; the trust
/// profile's tool-call policy is enforced via <see cref="TrustToolCallAuthorizer"/>.
/// </remarks>
public sealed class LocalTrustedExecutor : ITrustedExecutor
{
    /// <inheritdoc />
    public bool CanExecute(string targetClientInstance)
    {
        ArgumentNullException.ThrowIfNull(targetClientInstance);
        return string.Equals(targetClientInstance, TrustProfile.LocalClientInstance, StringComparison.Ordinal);
    }

    /// <summary>Creates a tool-call authorizer enforcing the request's trust profile.</summary>
    public static TrustToolCallAuthorizer CreateToolCallAuthorizer(TrustProfile trustProfile)
        => new(trustProfile);

    /// <inheritdoc />
    public Task<AgentChat> CreateAgentChatAsync(
        TrustedExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!this.CanExecute(request.TargetClientInstance))
        {
            throw new InvalidOperationException(
                $"LocalTrustedExecutor cannot execute on client instance '{request.TargetClientInstance}'.");
        }

        return AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = request.AgentDefinition,
            AgentSessionId = request.AgentSessionId,
            AgentServices = request.AgentServices,
        });
    }
}
