using System.Collections.Immutable;

namespace Phantom.Workspaces.Llm;

public sealed record LlmConversation
{
    public required ImmutableList<LlmEvent> Events { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public static LlmConversation Create(
        IEnumerable<LlmEvent>? events = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null)
    {
        var eventList = events?.ToImmutableList() ?? ImmutableList<LlmEvent>.Empty;
        var created = createdAt ?? DateTimeOffset.UtcNow;
        var updated = updatedAt ?? (eventList.Count > 0 ? eventList[^1].EndTime : created);
        return new LlmConversation
        {
            Events = eventList,
            CreatedAt = created,
            UpdatedAt = updated,
        };
    }
}
