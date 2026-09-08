using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Services;

public sealed class AcquireAgentChatRequest
{
    public required AgentSessionId AgentSessionId { get; init; }
    public JsonElement? AgentSessionEntity { get; init; }
    public AgentDefinition? AgentDefinition { get; init; }
    public AgentManifest? AgentManifest { get; init; }
    public AgentServices? AgentServices { get; init; }
    public TaskScheduler? ForegroundScheduler { get; init; }
    public IToolResourceFactory? ToolResourceFactory { get; init; }
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
    public IAgentDefinitionResolver? AgentDefinitionResolver { get; init; }
    public string EntityName { get; init; } = string.Empty;
    public string? EntityId { get; init; }
    public string? EntityDisplayName { get; init; }
    public string? EntityDescription { get; init; }

    /// <summary>
    /// Owning workspace-pane id (the pane the session is being started/opened in).
    /// Threaded into <see cref="RunningAgentChatWithEntityInfo.WorkspaceId"/> so cross-workspace
    /// status-button navigation (#1135) can switch to the owning workspace before focusing the agent.
    /// </summary>
    public string? WorkspaceId { get; init; }

    /// <summary>
    /// Acquisition mode (issue #1485). Local mode uses the in-process engine and forbids
    /// <see cref="OwningProfileTransport"/>; remote modes require a non-null owning transport and a
    /// persisted owner/generation on the entity.
    /// </summary>
    public AgentChatAcquisitionMode AcquisitionMode { get; init; } = AgentChatAcquisitionMode.Local;

    /// <summary>
    /// Non-owned reference to the transport that anchors the owning profile for a remote
    /// acquisition (issue #1485). The request does not clone or dispose the transport.
    /// </summary>
    public ITransport? OwningProfileTransport { get; init; } = null;

    /// <summary>
    /// Optional replay cursor when re-attaching to an already-running remote session (issue #1485).
    /// </summary>
    public ReplayCursor? ReplayCursor { get; init; } = null;
}
