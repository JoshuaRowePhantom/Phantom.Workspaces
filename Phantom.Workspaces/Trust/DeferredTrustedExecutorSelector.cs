using System;
using Phantom.Workspaces.Llm.Core.Transport;
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
    private volatile ExecutorTopology? topology;

    /// <summary>The transport-backed executor used for non-local targets, or null before initialization.</summary>
    public ITrustedExecutor? RemoteExecutor => this.remoteExecutor;

    /// <summary>
    /// The topology mapping each <see cref="ExecutorTarget"/> to a client instance. When set,
    /// <see cref="SelectExecutor(TrustProfile, string)"/> and
    /// <see cref="SelectExecutorForTarget(ExecutorTarget)"/> honor the topology to route
    /// GUI-local-tagged tools to <see cref="LocalTrustedExecutor"/> even when the remote executor
    /// is available. Default is <c>null</c> (single-machine behavior).
    /// </summary>
    public ExecutorTopology? Topology => this.topology;

    /// <summary>
    /// Supplies the transport-backed executor used for non-local targets. Called once the transport
    /// composition has been built during GUI initialization.
    /// </summary>
    public void SetRemoteExecutor(ITrustedExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);
        this.remoteExecutor = executor;
    }

    /// <summary>
    /// Sets the <see cref="ExecutorTopology"/> that governs which executor is selected for a given
    /// <see cref="ExecutorTarget"/>. When topology is set, <see cref="SelectExecutorForTarget"/>
    /// routes <see cref="ExecutorTarget.GuiLocal"/>-tagged tools to <see cref="LocalTrustedExecutor"/>
    /// (even if the remote executor is available), while other targets route to
    /// <see cref="RemoteExecutor"/>. Default is <c>null</c> (single-machine behavior).
    /// </summary>
    public void SetTopology(ExecutorTopology? topology)
    {
        this.topology = topology;
    }

    /// <summary>
    /// Selects the executor for a given <paramref name="executorTarget"/> execution class, honoring
    /// the current <see cref="Topology"/> if set. When topology is <c>null</c> or
    /// <see cref="ExecutorTopology.SingleMachine"/>, all targets route based on
    /// <see cref="ITrustedExecutor.CanExecute"/> (backward-compatible behavior). When topology is
    /// router-local, <see cref="ExecutorTarget.GuiLocal"/>-tagged tools route to
    /// <see cref="LocalTrustedExecutor"/>, while other targets route to <see cref="RemoteExecutor"/>.
    /// </summary>
    public ITrustedExecutor SelectExecutorForTarget(ExecutorTarget executorTarget)
    {
        var effectiveTopology = this.topology ?? ExecutorTopology.SingleMachine;

        if (effectiveTopology.ResolvesLocally(executorTarget))
        {
            return this.localExecutor;
        }

        if (this.remoteExecutor is { } remote)
        {
            return remote;
        }

        throw new InvalidOperationException(
            $"No remote executor is available for execution target '{executorTarget}'.");
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
