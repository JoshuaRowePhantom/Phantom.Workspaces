using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// A single chat history entry (user or completed assistant turn).
/// </summary>
public sealed record AgentChatHistoryItem
{
    public AgentDocumentOrigin? DocumentOrigin { get; init; }
    public static ChatRole DiagnosticChatRole { get; } = new("diagnostic");
    public static ChatRole HelpChatRole { get; } = new("help");

    public ChatRole Role { get; init; }

    /// <summary>
    /// UTC time at which this item was originally created.
    /// <see langword="null"/> means the timestamp is unknown (e.g. loaded from legacy history that
    /// predates timestamp support).
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Stable identity of the assistant invocation that produced this item, if known.</summary>
    public string? AssistantRunId { get; init; }

    /// <summary>Structured content blocks for this turn.</summary>
    public IReadOnlyList<AIContent> Contents { get; init; } = [];
}

public sealed record AgentDocumentOrigin(string? WorkingDirectory, bool IsLocalFileSystem,
    string? OwnerProfileId = null, string? MachineName = null)
{
    internal const string PropertyName = "phantomDocumentOrigin";

    internal static AgentDocumentOrigin? Read(ChatMessage message)
    {
        if (message.AdditionalProperties?.TryGetValue(PropertyName, out var value) != true)
            return null;
        if (value is AgentDocumentOrigin origin)
            return origin;
        if (value is System.Text.Json.JsonElement json)
        {
            try { return json.Deserialize<AgentDocumentOrigin>(AIJsonUtilities.DefaultOptions); }
            catch (System.Text.Json.JsonException) { }
        }
        return null;
    }
}
