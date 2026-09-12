using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Llm.Remote;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Captures the remote-acquisition inputs that must survive from the workspace host into the
/// running-agent creation path. The request does not own the transport; it simply forwards the
/// host-selected remote runtime anchor and optional replay cursor for later remote-transport work.
/// </summary>
public sealed record RemoteRuntimeIntent
{
    public required AgentChatAcquisitionMode AcquisitionMode { get; init; }

    public required ITransport OwningProfileTransport { get; init; }

    public ReplayCursor? ReplayCursor { get; init; }
}
