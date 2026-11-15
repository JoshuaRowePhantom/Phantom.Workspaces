using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm;

/// <summary>
/// Run-owned aggregation over new material. Immutable output strings and item arrays are
/// materialized separately; requesting every growing prefix necessarily copies those outputs.
/// Neither persistence nor projection replays retained updates to obtain the next version.
/// </summary>
internal sealed class StreamingResponseAccumulator(
    TimeProvider timeProvider,
    string? assistantRunId = null,
    AgentDocumentOrigin? documentOrigin = null)
{
    private readonly List<Message> messages = [];
    private readonly Dictionary<(ChatRole Role, string? Author, string? Agent, string Id), Message> identities = [];
    private readonly HashSet<int> changedIndices = [];
    private Message? current;

    internal long UpdateVisits { get; private set; }
    internal long ContentVisits { get; private set; }
    internal long AggregatedCharacters { get; private set; }
    internal long MaterializedCharacters { get; private set; }
    internal long SnapshotReferences { get; private set; }
    internal int Count => this.messages.Count;
    internal bool LastWasToolResult { get; private set; }
    internal StreamingAggregationWork Measure() => new()
    {
        UpdateVisits = this.UpdateVisits, ContentVisits = this.ContentVisits,
        AggregatedCharacters = this.AggregatedCharacters, MaterializedCharacters = this.MaterializedCharacters,
        SnapshotReferences = this.SnapshotReferences,
    };

    internal void Add(ChatResponseUpdate update) => this.Add(new AgentResponseUpdate
    {
        Role = update.Role, AuthorName = update.AuthorName, MessageId = update.MessageId,
        CreatedAt = update.CreatedAt, FinishReason = update.FinishReason, Contents = update.Contents,
    });

    internal void Add(AgentResponseUpdate update)
    {
        this.UpdateVisits++;
        this.LastWasToolResult = update.Contents.OfType<ToolResultContent>().Any();
        if (update.Contents.Count > 0)
        {
            Message? target = null;
            var replacement = RootTextReconciliation.IsReplacement(update.Contents);
            if (update.MessageId is { } identifier)
                this.identities.TryGetValue((update.Role ?? ChatRole.Assistant, update.AuthorName, update.AgentId, identifier), out target);
            if (target is null)
            {
                if (this.current is null || !this.current.CanAccept(update))
                {
                    this.current = new Message(this.messages.Count, update, timeProvider.GetUtcNow());
                    this.messages.Add(this.current);
                    if (update.MessageId is { } id)
                        this.identities.TryAdd((update.Role ?? ChatRole.Assistant, update.AuthorName, update.AgentId, id), this.current);
                }

                target = this.current;
                if (target.MessageId is null && update.MessageId is { } adoptedId)
                {
                    target.MessageId = adoptedId;
                    this.identities.TryAdd((update.Role ?? ChatRole.Assistant, update.AuthorName, update.AgentId, adoptedId), target);
                }
            }
            foreach (var content in update.Contents)
            {
                this.ContentVisits++;
                if (content is TextContent text) this.AggregatedCharacters += text.Text.Length;
                if (replacement && content is TextContent authoritative)
                    target.ReplaceText(authoritative);
                else target.Append(content);
            }
            this.changedIndices.Add(target.Index);
        }
        if (update.FinishReason is not null) this.current = null;
    }

    internal void AddInterstitial(AgentChatHistoryItem item)
    {
        this.current = null;
        if (item.Role != ChatRole.User && item.AssistantRunId is null)
            item = item with { AssistantRunId = assistantRunId, DocumentOrigin = documentOrigin };
        var message = new Message(this.messages.Count, item);
        this.messages.Add(message);
        this.changedIndices.Add(message.Index);
        this.LastWasToolResult = false;
    }

    internal int[] TakeChangedIndices()
    {
        var result = this.changedIndices.ToArray();
        this.changedIndices.Clear();
        return result;
    }

    internal AgentChatHistoryItem[] Snapshot()
    {
        var result = new AgentChatHistoryItem[this.messages.Count];
        this.SnapshotReferences += result.Length;
        for (var index = 0; index < result.Length; index++) result[index] = this.GetItem(index);
        return result;
    }

    internal AgentChatHistoryItem GetItem(int index)
    {
        var message = this.messages[index];
        if (message.Item is null)
        {
            var contents = message.MaterializeContents(this);
            message.Item = new()
            {
                Role = message.Role, MessageId = message.MessageId, Contents = contents,
                Timestamp = message.Timestamp,
                AssistantRunId = message.Role == ChatRole.User ? null : assistantRunId,
                DocumentOrigin = documentOrigin,
            };
        }
        return message.Item;
    }

    internal ChatMessage GetMessage(int index)
    {
        var message = this.messages[index];
        var item = this.GetItem(index);
        return new(message.Role, item.Contents.ToArray())
        {
            MessageId = message.MessageId, AuthorName = message.AuthorName, CreatedAt = message.Timestamp,
        };
    }

    private sealed class TextPart(TextContent first)
    {
        internal StringBuilder Text { get; } = new(first.Text);
        internal AdditionalPropertiesDictionary? Properties { get; } = first.AdditionalProperties;
    }

    private sealed class Message
    {
        private readonly List<object> contents = [];
        private readonly string? agentId;
        internal int Index { get; }
        internal ChatRole Role { get; }
        internal string? AuthorName { get; }
        internal string? MessageId { get; set; }
        internal DateTimeOffset? Timestamp { get; }
        internal AgentChatHistoryItem? Item { get; set; }

        internal Message(int index, AgentResponseUpdate first, DateTimeOffset now)
        {
            this.Index = index;
            this.Role = first.Role ?? ChatRole.Assistant;
            this.AuthorName = first.AuthorName;
            this.agentId = first.AgentId;
            this.MessageId = first.MessageId;
            this.Timestamp = first.CreatedAt ?? now;
        }

        internal Message(int index, AgentChatHistoryItem item)
        {
            this.Index = index;
            this.Role = item.Role;
            this.MessageId = item.MessageId;
            this.Timestamp = item.Timestamp;
            this.Item = item;
        }

        internal bool CanAccept(AgentResponseUpdate next) =>
            (next.Role ?? ChatRole.Assistant) == this.Role
            && next.AuthorName == this.AuthorName && next.AgentId == this.agentId
            && (this.MessageId is null || next.MessageId is null || next.MessageId == this.MessageId);

        internal void Append(AIContent content)
        {
            this.Item = null;
            if (content is TextContent text)
            {
                if (this.contents.Count > 0 && this.contents[^1] is TextPart tail) tail.Text.Append(text.Text);
                else this.contents.Add(new TextPart(text));
            }
            else this.contents.Add(content);
        }

        internal void ReplaceText(TextContent text)
        {
            this.Item = null;
            var inserted = false;
            var destination = 0;
            for (var index = 0; index < this.contents.Count; index++)
            {
                if (this.contents[index] is not TextPart)
                    this.contents[destination++] = this.contents[index];
                else if (!inserted)
                {
                    this.contents[destination++] = new TextPart(text);
                    inserted = true;
                }
            }
            this.contents.RemoveRange(destination, this.contents.Count - destination);
            if (!inserted) this.contents.Add(new TextPart(text));
        }

        internal AIContent[] MaterializeContents(StreamingResponseAccumulator owner)
        {
            var result = new AIContent[this.contents.Count];
            for (var index = 0; index < result.Length; index++)
            {
                if (this.contents[index] is TextPart text)
                {
                    var value = text.Text.ToString();
                    owner.MaterializedCharacters += value.Length;
                    result[index] = new TextContent(value) { AdditionalProperties = text.Properties };
                }
                else result[index] = (AIContent)this.contents[index];
            }
            return result;
        }
    }
}
