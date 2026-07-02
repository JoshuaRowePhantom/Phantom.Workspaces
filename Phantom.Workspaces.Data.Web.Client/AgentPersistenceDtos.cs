using System.Text.Json;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Data.Web.Client;

public sealed record PersistedAgentDto
{
    public required string AgentSessionId { get; init; }

    public JsonElement? AgentSessionJson { get; init; }

    public JsonElement? AgentDefinitionJson { get; init; }

    public string? CopilotSdkSessionId { get; init; }
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

public sealed record SubAgentManifestEntryDto
{
    public required string SessionId { get; init; }

    public required JsonElement AgentDefinitionJson { get; init; }

    public required AgentChatCompletionState CompletionState { get; init; }

    public required DateTime LastUpdatedAt { get; init; }
}

public sealed record ReadSubAgentManifestRequest
{
    public required string ParentSessionId { get; init; }
}

public sealed record ReadSubAgentManifestResponse
{
    public required SubAgentManifestEntryDto[] Entries { get; init; }
}

public sealed record WriteSubAgentManifestEntryRequest
{
    public required string ParentSessionId { get; init; }

    public required SubAgentManifestEntryDto Entry { get; init; }
}
