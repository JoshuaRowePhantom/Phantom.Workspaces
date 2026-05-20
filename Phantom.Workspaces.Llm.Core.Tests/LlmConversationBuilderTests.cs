using System.Collections.Immutable;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class LlmConversationBuilderTests
{
    [Fact]
    public void Build_PreservesCanonicalEventOrdering()
    {
        var builder = LlmConversationBuilder
            .Create()
            .AddEvent(
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Token,
                    Role = LlmRoles.Assistant,
                    Content = "Hello ",
                })
            .AddEvent(
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Token,
                    Role = LlmRoles.Assistant,
                    Content = "world",
                    Thinking = "step1",
                })
            .AddEvent(
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Token,
                    Role = LlmRoles.Assistant,
                    Thinking = "step2",
                });

        var conversation = builder.Build();

        Assert.Equal(3, conversation.Events.Count);
        Assert.Equal("Hello ", conversation.Events[0].Content);
        Assert.Equal("world", conversation.Events[1].Content);
        Assert.Equal("step2", conversation.Events[2].Thinking);
    }

    [Fact]
    public void Build_PreservesToolEventPayloads()
    {
        var builder = LlmConversationBuilder
            .Create()
            .AddEvent(
                new LlmEvent
                {
                    EventKind = LlmEventKinds.ToolCall,
                    ToolCalls =
                    [
                        new LlmEvent
                        {
                            EventKind = LlmEventKinds.ToolCall,
                            ToolName = "read_file",
                            Content = """{"path":"x"}""",
                        },
                    ],
                })
            .AddEvent(
                new LlmEvent
                {
                    EventKind = LlmEventKinds.ToolResult,
                    Role = LlmRoles.Tool,
                    ToolName = "read_file",
                    Content = """{"ok":true}""",
                    ExternalContent = "blob://artifact",
                    ExternalContentName = "artifact.json",
                });

        var conversation = builder.Build();

        Assert.Equal(2, conversation.Events.Count);
        Assert.Single(conversation.Events[0].ToolCalls!);
        Assert.Equal("read_file", conversation.Events[0].ToolCalls![0].ToolName);
        Assert.Equal("artifact.json", conversation.Events[1].ExternalContentName);
    }

    [Fact]
    public void ReplaceTail_ReplacesTrailingEvents()
    {
        var builder = LlmConversationBuilder
            .Create()
            .AddEvent(
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.User,
                    Content = "first",
                })
            .AddEvent(
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.User,
                    Content = "second",
                });

        var conversation = builder.ReplaceTail(
            1,
            [
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.User,
                    Content = "replacement",
                },
            ]).Build();

        Assert.Equal(2, conversation.Events.Count);
        Assert.Equal("first", conversation.Events[0].Content);
        Assert.Equal("replacement", conversation.Events[1].Content);
    }

    [Fact]
    public void AddStreamEvent_ReplaceTailReplacesEvents()
    {
        var builder = LlmConversationBuilder
            .Create()
            .AddEvent(
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.User,
                    Content = "first",
                })
            .AddEvent(
                new LlmEvent
                {
                    EventKind = LlmEventKinds.Turn,
                    Role = LlmRoles.User,
                    Content = "second",
                });

        var conversation = builder.AddStreamEvent(
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
                            Content = "stream-replacement",
                        },
                    ],
                },
            }).Build();

        Assert.Equal(2, conversation.Events.Count);
        Assert.Equal("first", conversation.Events[0].Content);
        Assert.Equal("stream-replacement", conversation.Events[1].Content);
    }

    [Fact]
    public void AddStreamEvent_CheckpointUsesCheckpointConversation()
    {
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

        var conversation = LlmConversationBuilder
            .Create()
            .AddStreamEvent(
                new LlmStreamEvent
                {
                    Checkpoint = new LlmCheckpointEvent
                    {
                        Conversation = checkpointConversation,
                    },
                })
            .Build();

        Assert.Single(conversation.Events);
        Assert.Equal("checkpoint", conversation.Events[0].Content);
    }
}
