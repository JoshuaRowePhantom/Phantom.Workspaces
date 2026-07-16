using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm.Core.Transport;

/// <summary>
/// Maps each <see cref="ExecutorTarget"/> execution class to the client instance (machine) it runs
/// on. In the common single-machine topology (G == H == E) all three targets resolve to the local
/// client instance (<c>"."</c>), so routing introduces no additional transport round-trips.
/// </summary>
public sealed record ExecutorTopology
{
    /// <summary>Client instance for <see cref="ExecutorTarget.AgentExecutor"/> (executor instance E).</summary>
    public string AgentExecutorClientInstance { get; init; } = TrustProfile.LocalClientInstance;

    /// <summary>Client instance for <see cref="ExecutorTarget.GuiLocal"/> (GUI/initiating machine G).</summary>
    public string GuiLocalClientInstance { get; init; } = TrustProfile.LocalClientInstance;

    /// <summary>Client instance for <see cref="ExecutorTarget.HostingInstance"/> (hosting instance H).</summary>
    public string HostingInstanceClientInstance { get; init; } = TrustProfile.LocalClientInstance;

    /// <summary>The single-machine topology: all three execution classes resolve to the local instance.</summary>
    public static ExecutorTopology SingleMachine { get; } = new();

    /// <summary>Resolves the client instance a given execution class runs on.</summary>
    public string Resolve(ExecutorTarget target) => target switch
    {
        ExecutorTarget.AgentExecutor => this.AgentExecutorClientInstance,
        ExecutorTarget.GuiLocal => this.GuiLocalClientInstance,
        ExecutorTarget.HostingInstance => this.HostingInstanceClientInstance,
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown executor target."),
    };

    /// <summary>Whether the given execution class resolves to the local client instance (<c>"."</c>).</summary>
    public bool ResolvesLocally(ExecutorTarget target)
        => ExecutionTargetResolver.IsLocal(this.Resolve(target));

    /// <summary>
    /// Whether every execution class resolves to the same client instance — the behavior-preserving
    /// single-machine case in which no cross-machine routing (and no added round-trip) occurs.
    /// </summary>
    public bool IsSingleMachine
        => string.Equals(this.AgentExecutorClientInstance, this.GuiLocalClientInstance, StringComparison.Ordinal)
           && string.Equals(this.AgentExecutorClientInstance, this.HostingInstanceClientInstance, StringComparison.Ordinal);
}
