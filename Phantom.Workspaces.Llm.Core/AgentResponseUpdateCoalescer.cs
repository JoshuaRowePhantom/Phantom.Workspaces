using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm;

// Segments an ordered slice of streamed AgentResponseUpdates into AgentChatHistoryItems
// without ever consulting Microsoft.Extensions.AI.ToChatResponseAsync. See issue #1221:
// the SDK's null-MessageId grouping silently drops TextContent when a same-role tool-call
// update follows it. This coalescer preserves every content payload in stream order.
internal static class AgentResponseUpdateCoalescer
{
    public static AgentChatHistoryItem[] Coalesce(
        AgentResponseUpdate[] updates,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (updates.Length == 0)
        {
            return [];
        }

        var result = new List<AgentChatHistoryItem>();
        MessageAccumulator? current = null;

        foreach (var update in updates)
        {
            var contents = update.Contents;
            if (contents is null || contents.Count == 0)
            {
                // Terminal / metadata-only update (e.g. FinishReason = Stop).
                if (update.FinishReason is not null && current is not null)
                {
                    result.Add(current.Build(timeProvider));
                    current = null;
                }

                continue;
            }

            if (current is null || !current.CanAccept(update))
            {
                if (current is not null)
                {
                    result.Add(current.Build(timeProvider));
                }

                current = new MessageAccumulator(update);
            }

            current.Append(contents);

            if (update.FinishReason is not null)
            {
                result.Add(current.Build(timeProvider));
                current = null;
            }
        }

        if (current is not null)
        {
            result.Add(current.Build(timeProvider));
        }

        return result.ToArray();
    }

    private sealed class MessageAccumulator
    {
        private readonly ChatRole role;
        private readonly string? authorName;
        private readonly string? agentId;
        private readonly string? messageId;
        private readonly DateTimeOffset? earliestCreatedAt;
        private readonly List<AIContent> contents = new();

        public MessageAccumulator(AgentResponseUpdate first)
        {
            this.role = first.Role ?? ChatRole.Assistant;
            this.authorName = first.AuthorName;
            this.agentId = first.AgentId;
            this.messageId = first.MessageId;
            this.earliestCreatedAt = first.CreatedAt;
        }

        // Two updates belong to the same message iff their Role, AuthorName, AgentId, and
        // (if both non-null) MessageId all agree. Any non-null id mismatch splits.
        public bool CanAccept(AgentResponseUpdate next)
        {
            if ((next.Role ?? ChatRole.Assistant) != this.role)
            {
                return false;
            }

            if (!string.Equals(next.AuthorName, this.authorName, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(next.AgentId, this.agentId, StringComparison.Ordinal))
            {
                return false;
            }

            if (this.messageId is not null && next.MessageId is not null
                && !string.Equals(this.messageId, next.MessageId, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        public void Append(IList<AIContent> incoming)
        {
            foreach (var item in incoming)
            {
                // Fold consecutive TextContent so History items stay compact.
                if (item is TextContent text
                    && this.contents.Count > 0
                    && this.contents[^1] is TextContent tail)
                {
                    this.contents[^1] = new TextContent(tail.Text + text.Text)
                    {
                        AdditionalProperties = tail.AdditionalProperties,
                    };
                    continue;
                }

                this.contents.Add(item);
            }
        }

        public AgentChatHistoryItem Build(TimeProvider timeProvider)
            => new()
            {
                Role = this.role,
                Contents = this.contents.ToArray(),
                Timestamp = this.earliestCreatedAt ?? timeProvider.GetUtcNow(),
            };
    }
}
