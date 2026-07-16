using System.Text.Json;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Core.Transport;

/// <summary>
/// Routes a per-tool <see cref="ExecutorTarget"/> to the correct machine: it maps the execution
/// class to a client instance via an <see cref="ExecutorTopology"/>, turns that into a transport
/// connection descriptor via <see cref="ExecutionTargetResolver"/>, and connects through an
/// <see cref="ITransportFactoryRegistry"/>. This is the landed-transport equivalent of the
/// pre-cutover <c>ChatClientTransportListener</c> "route mcp-servers by execution-target" seam:
/// agent-executor tools route to E, gui-local tools route back to the caller/GUI machine G, and
/// hosting-instance tools route to H. In a single-machine topology every target resolves to
/// <c>{"type":"local"}</c>, so no additional round-trips are introduced.
/// </summary>
public sealed class ExecutorTargetRouter
{
    private readonly ExecutorTopology topology;
    private readonly ITransportFactoryRegistry transportFactoryRegistry;
    private readonly ExecutionTargetResolver executionTargetResolver;

    public ExecutorTargetRouter(
        ExecutorTopology topology,
        ITransportFactoryRegistry transportFactoryRegistry,
        ExecutionTargetResolver? executionTargetResolver = null)
    {
        this.topology = topology ?? throw new ArgumentNullException(nameof(topology));
        this.transportFactoryRegistry = transportFactoryRegistry ?? throw new ArgumentNullException(nameof(transportFactoryRegistry));
        this.executionTargetResolver = executionTargetResolver ?? new ExecutionTargetResolver();
    }

    /// <summary>The topology used to resolve execution classes to client instances.</summary>
    public ExecutorTopology Topology => this.topology;

    /// <summary>Resolves the client instance a given execution class runs on.</summary>
    public string ResolveClientInstance(ExecutorTarget target) => this.topology.Resolve(target);

    /// <summary>Whether the given execution class resolves to the local machine (no round-trip).</summary>
    public bool ResolvesLocally(ExecutorTarget target) => this.topology.ResolvesLocally(target);

    /// <summary>Builds the transport connection descriptor for a given execution class.</summary>
    public JsonElement ResolveDescriptor(ExecutorTarget target)
        => this.executionTargetResolver.ResolveDescriptor(this.topology.Resolve(target));

    /// <summary>Connects a transport to the machine the given execution class runs on.</summary>
    public Task<ITransport> ConnectAsync(ExecutorTarget target, CancellationToken ct = default)
        => this.transportFactoryRegistry.ConnectToAsync(this.ResolveDescriptor(target), ct);
}
