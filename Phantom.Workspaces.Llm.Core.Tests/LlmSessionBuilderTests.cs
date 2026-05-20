namespace Phantom.Workspaces.Llm.Tests;

public sealed class LlmSessionBuilderTests
{
    [Fact]
    public void AddStreamEvent_CheckpointAddsBrandNewConversation()
    {
        var builder = LlmSessionBuilder.Create();
        var checkpointConversation = LlmConversation.Create(
            events:
            [
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.System,
                    Content = "checkpoint",
                },
            ]);

        var updatedBuilder = builder
            .AddEvent(
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.User,
                    Content = "hello",
                })
            .AddStreamEvent(
                new LlmStreamEvent
                {
                    Checkpoint = new LlmCheckpointEvent
                    {
                        Conversation = checkpointConversation,
                    },
                });

        var session = updatedBuilder.Build();

        Assert.Equal(2, session.Conversations.Count);
        Assert.Equal("hello", session.Conversations[0].Events[0].Content);
        Assert.Equal("checkpoint", session.Conversations[1].Events[0].Content);
    }

    [Fact]
    public void AddStreamEvent_ReplaceTailReplacesEvents()
    {
        var builder = LlmSessionBuilder
            .Create()
            .AddEvent(
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.User,
                    Content = "original",
                })
            .AddStreamEvent(
                new LlmStreamEvent
                {
                    Replace = new LlmReplaceEvent
                    {
                        RemoveCount = 1,
                        Events =
                        [
                            new LlmEvent
                            {
                                EventKind = LlmEventKinds.Turn,
                                Role = LlmRoles.User,
                                Content = "replacement",
                            },
                        ],
                    },
                });

        var session = builder.Build();
        Assert.Single(session.Conversations);
        Assert.Single(session.Conversations[0].Events);
        Assert.Equal("replacement", session.Conversations[0].Events[0].Content);
    }
}
