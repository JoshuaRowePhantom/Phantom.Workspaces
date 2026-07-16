using System;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Trust;

/// <summary>
/// The production <see cref="ITrustedExecutorSelector"/> for the GUI. Local execution
/// (<c>"."</c>) is served by the retained <see cref="LocalTrustedExecutor"/>; remote execution is
/// served by the transport-backed executor (a <c>TransportTrustedExecutor</c> resolved from the
/// <c>ITransportFactoryRegistry</c>), which is supplied via <see cref="SetRemoteExecutor"/> once the
/// asynchronously-initialised transport composition is available. Replaces
/// <c>TrustedExecutorComposition.CreateSelector(reverseExecutionRegistry)</c> — no
/// <c>ReverseExecutionRegistry</c> / <c>ReverseTrustedExecutor</c> is involved.
/// </summary>
public sealed class DeferredTrustedExecutorSelector : ITrustedExecutorSelector
{
    private readonly LocalTrustedExecutor localExecutor = new();
    private volatile ITrustedExecutor? remoteExecutor;

    /// <summary>The transport-backed executor used for non-local targets, or null before initialization.</summary>
    public ITrustedExecutor? RemoteExecutor => this.remoteExecutor;

    /// <summary>
    /// Supplies the transport-backed executor used for non-local targets. Called once the transport
    /// composition has been built during GUI initialization.
    /// </summary>
    public void SetRemoteExecutor(ITrustedExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
        this.remoteExecutor = executor;
    }

    /// <inheritdoc />
    public ITrustedExecutor SelectExecutor(TrustProfile trustProfile, string targetClientInstance)
    {
        ArgumentNullException.ThrowIfNull(trustProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetClientInstance);

        if (!trustProfile.AllowsClientInstance(targetClientInstance))
        {
            throw new InvalidOperationException(
                $"Trust profile does not permit execution on client instance '{targetClientInstance}'.");
        }

        if (this.localExecutor.CanExecute(targetClientInstance))
        {
            return this.localExecutor;
        }

        if (this.remoteExecutor is { } remote && remote.CanExecute(targetClientInstance))
        {
            return remote;
        }

        throw new InvalidOperationException(
            $"No trusted executor is available for client instance '{targetClientInstance}'.");
    }
}
