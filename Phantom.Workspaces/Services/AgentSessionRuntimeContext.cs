using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Services;

public sealed record AgentSessionRuntimeContext
{
    public required PersistedAgentSessionRuntimeIntent Intent { get; init; }

    public ITransportFactoryRegistry? TransportFactoryRegistry { get; init; } = null;
}
