using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

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
}
