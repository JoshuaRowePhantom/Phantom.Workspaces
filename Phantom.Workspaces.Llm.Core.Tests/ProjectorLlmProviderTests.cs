namespace Phantom.Workspaces.Llm.Tests;

public sealed class ProjectorLlmProviderTests
{
    [Fact]
    public async Task StreamAsync_CoalescesAssistantContentChunks()
    {
        var provider = new ProjectorLlmProvider(new TestProvider(
            TestProvider.Content("Hel"),
            TestProvider.Content("lo")));

        var streamEvents = new List<LlmStreamEvent>();
        await foreach (var streamEvent in provider.StreamAsync(LlmConversation.Create()))
        {
            streamEvents.Add(streamEvent);
        }

        Assert.Collection(
            streamEvents,
            first =>
            {
                Assert.Equal("Hel", first.Event?.Content);
                Assert.Null(first.Replace);
            },
            second =>
            {
                Assert.Null(second.Event);
                Assert.NotNull(second.Replace);
                Assert.Equal(1, second.Replace!.RemoveCount);
                Assert.Single(second.Replace.Events);
                Assert.Equal("Hello", second.Replace.Events[0].Content);
            });
    }

    [Fact]
    public async Task StreamAsync_CoalescesAssistantThinkingChunks()
    {
        var provider = new ProjectorLlmProvider(new TestProvider(
            new LlmStreamEvent
            {
                Event = TestProvider.AssistantTurn(thinking: "Thin"),
            },
            new LlmStreamEvent
            {
                Event = TestProvider.AssistantTurn(thinking: "king"),
            }));

        var streamEvents = new List<LlmStreamEvent>();
        await foreach (var streamEvent in provider.StreamAsync(LlmConversation.Create()))
        {
            streamEvents.Add(streamEvent);
        }

        Assert.Collection(
            streamEvents,
            first =>
            {
                Assert.Equal("Thin", first.Event?.Thinking);
                Assert.Null(first.Replace);
            },
            second =>
            {
                Assert.Null(second.Event);
                Assert.NotNull(second.Replace);
                Assert.Equal(1, second.Replace!.RemoveCount);
                Assert.Single(second.Replace.Events);
                Assert.Equal("Thinking", second.Replace.Events[0].Thinking);
            });
    }

    [Fact]
    public async Task StreamAsync_PassesThroughCheckpoints()
    {
        var checkpointConversation = LlmConversation.Create(
            [
                TestProvider.UserTurn("hello"),
            ]);

        var provider = new ProjectorLlmProvider(new TestProvider(
            TestProvider.Content("Hel"),
            TestProvider.Checkpoint(checkpointConversation)));

        var streamEvents = new List<LlmStreamEvent>();
        await foreach (var streamEvent in provider.StreamAsync(LlmConversation.Create()))
        {
            streamEvents.Add(streamEvent);
        }

        Assert.Collection(
            streamEvents,
            first => Assert.Equal("Hel", first.Event?.Content),
            second =>
            {
                Assert.Null(second.Event);
                Assert.NotNull(second.Checkpoint);
                Assert.Same(checkpointConversation, second.Checkpoint!.Conversation);
            });
    }

}
