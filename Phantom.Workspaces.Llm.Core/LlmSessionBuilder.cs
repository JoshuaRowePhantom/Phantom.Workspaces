using System.Collections.Immutable;

namespace Phantom.Workspaces.Llm;

public sealed class LlmSessionBuilder
{
    public ImmutableList<LlmConversationBuilder> Conversations { get; }

    public DateTimeOffset CreatedAt { get; }

    private LlmSessionBuilder(
        ImmutableList<LlmConversationBuilder> conversations,
        DateTimeOffset createdAt)
    {
        this.Conversations = conversations;
        this.CreatedAt = createdAt;
    }

    public static LlmSessionBuilder Create()
    {
        return new LlmSessionBuilder(
            ImmutableList<LlmConversationBuilder>.Empty,
            DateTimeOffset.UtcNow);
    }

    public static LlmSessionBuilder FromSession(
        LlmSession session)
    {
        return new LlmSessionBuilder(
            session.Conversations
                .Select(LlmConversationBuilder.FromConversation)
                .ToImmutableList(),
            session.CreatedAt);
    }

    public LlmSessionBuilder AddEvent(
        LlmEvent llmEvent)
    {
        return this.AddStreamEvent(new LlmStreamEvent { Event = llmEvent });
    }

    public LlmSessionBuilder AddStreamEvent(
        LlmStreamEvent streamEvent)
    {
        var conversations = this.Conversations;
        if (streamEvent.Checkpoint is not null)
        {
            conversations = conversations.Add(
                LlmConversationBuilder.FromConversation(streamEvent.Checkpoint.Conversation));
        }
        else
        {
            if (conversations.Count == 0)
            {
                conversations = conversations.Add(LlmConversationBuilder.Create());
            }

            var lastIndex = conversations.Count - 1;
            var currentConversation = conversations[lastIndex].AddStreamEvent(streamEvent);

            conversations = conversations.SetItem(lastIndex, currentConversation);
        }

        return new LlmSessionBuilder(
            conversations,
            this.CreatedAt);
    }

    public LlmSessionBuilder AddEvents(
        IEnumerable<LlmEvent> llmEvents)
    {
        var builder = this;
        foreach (var llmEvent in llmEvents)
        {
            builder = builder.AddEvent(llmEvent);
        }

        return builder;
    }

    public LlmSessionBuilder AddStreamEvents(
        IEnumerable<LlmStreamEvent> streamEvents)
    {
        var builder = this;
        foreach (var streamEvent in streamEvents)
        {
            builder = builder.AddStreamEvent(streamEvent);
        }

        return builder;
    }

    public LlmSession Build()
    {
        var conversations = this.Conversations.Select(builder => builder.Build()).ToImmutableList();
        var updatedAt = conversations.Count > 0
            ? conversations.Max(static conversation => conversation.UpdatedAt)
            : this.CreatedAt;
        return new LlmSession
        {
            Conversations = conversations,
            CreatedAt = this.CreatedAt,
            UpdatedAt = updatedAt,
        };
    }
}
