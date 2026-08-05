using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Data.Web.Client;

public sealed record PersistedAgentDto
{
    public required string AgentSessionId { get; init; }

    public JsonElement? AgentSessionJson { get; init; }

    public JsonElement? AgentDefinitionJson { get; init; }

    public string? CopilotSdkSessionId { get; init; }

    /// <summary>
    /// The persisted last-activity/last-updated UTC timestamp for this agent session, or
    /// <see langword="null"/> when the server-side store does not track one. Preserved across the
    /// web boundary so the client-side <see cref="Phantom.Workspaces.Llm.Interfaces.PersistedAgent"/>
    /// keeps the original last-activity time on reload (issue #1140).
    /// </summary>
    public DateTime? LastUpdatedUtc { get; init; }
}

public sealed record StoreAgentRequest
{
    public required PersistedAgentDto Agent { get; init; }

    public ChatMessage[]? NewMessages { get; init; }
}

public sealed record ReadMessagesResponse
{
    public required ChatMessage[] Messages { get; init; }
}

public sealed record AddSubAgentLinkRequest
{
    public required string ParentSessionId { get; init; }

    public required string ChildSessionId { get; init; }
}

public sealed record ReadSubAgentChildIdsRequest
{
    public required string ParentSessionId { get; init; }
}

public sealed record ReadSubAgentChildIdsResponse
{
    public required string[] ChildSessionIds { get; init; }
}
