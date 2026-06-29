using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Trust;

/// <summary>
/// An <see cref="ITrustedExecutor"/> that delegates to <see cref="RemoteExecutionRegistry"/>:
/// for each target client instance, it looks up the registered <see cref="RemoteTrustedExecutor"/>
/// and forwards execution to it. When no executor is registered for the target instance,
/// <see cref="CanExecute"/> returns <see langword="false"/> and <see cref="CreateAgentChatAsync"/>
/// throws.
/// </summary>
public sealed class DynamicRemoteTrustedExecutor : ITrustedExecutor
{
    private readonly RemoteExecutionRegistry registry;

    /// <summary>Creates a dynamic executor backed by the supplied registry.</summary>
    public DynamicRemoteTrustedExecutor(RemoteExecutionRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc />
    public bool CanExecute(string targetClientInstance)
    {
        ArgumentNullException.ThrowIfNull(targetClientInstance);

        // Never route the local instance through the remote HTTP path.
        return !string.Equals(targetClientInstance, TrustProfile.LocalClientInstance, StringComparison.Ordinal)
            && this.registry.IsRegistered(targetClientInstance);
    }

    /// <inheritdoc />
    public Task<AgentChat> CreateAgentChatAsync(
        TrustedExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!this.registry.TryGetExecutor(request.TargetClientInstance, out var executor))
        {
            throw new InvalidOperationException(
                $"No remote executor is registered for client instance '{request.TargetClientInstance}'.");
        }

        return executor.CreateAgentChatAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken ct = default)
        => throw new NotImplementedException(
               "OpenStreamAsync over the remote HTTP tunnel is not yet implemented.");
}
