namespace Phantom.Workspaces.Llm.Tests;

public sealed class TestProviderTests
{
    [Fact]
    public async Task StreamAsync_YieldsEventsInOrder()
    {
        var provider = new TestProvider(
            TestProvider.Content("one"),
            TestProvider.Content("two"));

        var observed = new List<LlmStreamEvent>();
        await foreach (var streamEvent in provider.StreamAsync(LlmConversation.Create()))
        {
            observed.Add(streamEvent);
        }

        Assert.Collection(
            observed,
            first => Assert.Equal("one", first.Event!.Content),
            second => Assert.Equal("two", second.Event!.Content));
    }

    [Fact]
    public void UserTurn_CreatesUserTurnEvent()
    {
        var eventItem = TestProvider.UserTurn("hello");

        Assert.Equal(LlmEventKinds.Turn, eventItem.EventKind);
        Assert.Equal(LlmRoles.User, eventItem.Role);
        Assert.Equal("hello", eventItem.Content);
    }

    [Fact]
    public void SystemTurn_CreatesSystemTurnEvent()
    {
        var eventItem = TestProvider.SystemTurn("system");

        Assert.Equal(LlmEventKinds.Turn, eventItem.EventKind);
        Assert.Equal(LlmRoles.System, eventItem.Role);
        Assert.Equal("system", eventItem.Content);
    }

    [Fact]
    public void AssistantTurn_CanSetContentAndThinking()
    {
        var eventItem = TestProvider.AssistantTurn("hello", "think");

        Assert.Equal(LlmEventKinds.Turn, eventItem.EventKind);
        Assert.Equal(LlmRoles.Assistant, eventItem.Role);
        Assert.Equal("hello", eventItem.Content);
        Assert.Equal("think", eventItem.Thinking);
    }

    [Fact]
    public void AssistantContentToken_CreatesTokenEvent()
    {
        var eventItem = TestProvider.AssistantContentToken("h");

        Assert.Equal(LlmEventKinds.Token, eventItem.EventKind);
        Assert.Equal(LlmRoles.Assistant, eventItem.Role);
        Assert.Equal("h", eventItem.Content);
    }

    [Fact]
    public void AssistantThinkingToken_CreatesTokenEvent()
    {
        var eventItem = TestProvider.AssistantThinkingToken("t");

        Assert.Equal(LlmEventKinds.Token, eventItem.EventKind);
        Assert.Equal(LlmRoles.Assistant, eventItem.Role);
        Assert.Equal("t", eventItem.Thinking);
    }

    [Fact]
    public void Content_CreatesAssistantContentStreamEvent()
    {
        var streamEvent = TestProvider.Content("hello");

        Assert.Equal("hello", streamEvent.Event!.Content);
        Assert.Equal(LlmEventKinds.Turn, streamEvent.Event.EventKind);
        Assert.Equal(LlmRoles.Assistant, streamEvent.Event.Role);
    }

    [Fact]
    public void ContentToken_CreatesAssistantContentTokenStreamEvent()
    {
        var streamEvent = TestProvider.ContentToken("h");

        Assert.Equal("h", streamEvent.Event!.Content);
        Assert.Equal(LlmEventKinds.Token, streamEvent.Event.EventKind);
        Assert.Equal(LlmRoles.Assistant, streamEvent.Event.Role);
    }

    [Fact]
    public void ThinkingToken_CreatesAssistantThinkingTokenStreamEvent()
    {
        var streamEvent = TestProvider.ThinkingToken("t");

        Assert.Equal("t", streamEvent.Event!.Thinking);
        Assert.Equal(LlmEventKinds.Token, streamEvent.Event.EventKind);
        Assert.Equal(LlmRoles.Assistant, streamEvent.Event.Role);
    }

    [Fact]
    public void ToolUse_CreatesToolCallStreamEvent()
    {
        var streamEvent = TestProvider.ToolUse("read_file", """{"path":"x"}""");

        var toolCall = Assert.Single(streamEvent.Event!.ToolCalls!);
        Assert.Equal(LlmEventKinds.ToolCall, streamEvent.Event.EventKind);
        Assert.Equal(LlmRoles.Assistant, streamEvent.Event.Role);
        Assert.Equal("read_file", toolCall.ToolName);
        Assert.Equal("""{"path":"x"}""", toolCall.Content);
    }

    [Fact]
    public void Checkpoint_CreatesCheckpointStreamEvent()
    {
        var conversation = LlmConversation.Create(
            [
                TestProvider.UserTurn("hello"),
            ]);

        var streamEvent = TestProvider.Checkpoint(conversation);

        Assert.Same(conversation, streamEvent.Checkpoint!.Conversation);
    }
}
