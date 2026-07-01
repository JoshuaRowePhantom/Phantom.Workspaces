using System.Text.Json;
using Microsoft.Extensions.AI;

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
