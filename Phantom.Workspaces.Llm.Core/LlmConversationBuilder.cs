using System.Collections.Immutable;

namespace Phantom.Workspaces.Llm;

public sealed class LlmConversationBuilder
{
    public DateTimeOffset CreatedAt { get; }

    public ImmutableList<LlmEvent> Events { get; }

    private LlmConversationBuilder(
        DateTimeOffset createdAt,
        ImmutableList<LlmEvent> events)
    {
        this.CreatedAt = createdAt;
        this.Events = events;
    }

    public static LlmConversationBuilder Create()
    {
        return new LlmConversationBuilder(
            DateTimeOffset.UtcNow,
            ImmutableList<LlmEvent>.Empty);
    }

    public static LlmConversationBuilder FromConversation(
        LlmConversation conversation)
    {
        return new LlmConversationBuilder(
            conversation.CreatedAt,
            conversation.Events);
    }

    public LlmConversationBuilder AddEvent(
        LlmEvent llmEvent)
    {
        return new LlmConversationBuilder(
            this.CreatedAt,
            this.Events.Add(llmEvent));
    }

    public LlmConversationBuilder AddEvents(
        IEnumerable<LlmEvent> llmEvents)
    {
        return new LlmConversationBuilder(
            this.CreatedAt,
            this.Events.AddRange(llmEvents));
    }

    public LlmConversationBuilder AddStreamEvent(
        LlmStreamEvent streamEvent)
    {
        if (streamEvent.Checkpoint is not null)
        {
            return FromConversation(streamEvent.Checkpoint.Conversation);
        }

        if (streamEvent.Replace is not null)
        {
            return this.ReplaceTail(
                streamEvent.Replace.RemoveCount,
                streamEvent.Replace.Events);
        }

        if (streamEvent.Event is not null)
        {
            return this.AddEvent(streamEvent.Event);
        }

        return this;
    }

    public LlmConversationBuilder ReplaceTail(
        int removeCount,
        IEnumerable<LlmEvent> replacementEvents)
    {
        if (removeCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(removeCount));
        }

        if (removeCount > this.Events.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(removeCount));
        }

        var updatedEvents = this.Events.RemoveRange(this.Events.Count - removeCount, removeCount);
        return new LlmConversationBuilder(
            this.CreatedAt,
            updatedEvents.AddRange(replacementEvents));
    }

    public LlmConversation Build()
    {
        var updatedAt = this.Events.Count > 0
            ? this.Events[^1].Timestamp
            : this.CreatedAt;
        return LlmConversation.Create(
            this.Events,
            this.CreatedAt,
            updatedAt);
    }
}
