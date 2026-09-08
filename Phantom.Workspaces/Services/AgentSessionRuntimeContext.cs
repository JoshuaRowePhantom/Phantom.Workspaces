using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Services;

public sealed record AgentSessionRuntimeContext(
    ExecutorBindings ExecutorBindings,
    ITransportFactoryRegistry? TransportFactoryRegistry);
