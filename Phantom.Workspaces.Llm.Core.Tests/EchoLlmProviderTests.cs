using Phantom.Workspaces.Llm.Echo;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class EchoLlmProviderTests
{
    [Fact]
    public async Task StreamAsync_WhenGivenToolUsePrefix_YieldsToolCallEvent()
    {
        var provider = new EchoLlmProvider();
        var conversation = LlmConversation.Create(
            events:
            [
                TestProvider.UserTurn("tool_use: read_file {\"path\":\"x\"}"),
            ]);

        var streamEvents = await ReadAllAsync(provider, conversation);

        Assert.Single(streamEvents);
        var toolCall = Assert.Single(streamEvents[0].Event!.ToolCalls!);
        Assert.Equal(LlmEventKinds.ToolCall, streamEvents[0].Event!.EventKind);
        Assert.Equal(LlmRoles.Assistant, streamEvents[0].Event!.Role);
        Assert.Equal("read_file", toolCall.ToolName);
        Assert.Equal("{\"path\":\"x\"}", toolCall.Content);
    }

    [Fact]
    public async Task StreamAsync_WhenGivenContentTokensPrefix_YieldsOneTokenPerCharacter()
    {
        var provider = new EchoLlmProvider();
        var conversation = LlmConversation.Create(
            events:
            [
                TestProvider.UserTurn("content-tokens: abc"),
            ]);

        var streamEvents = await ReadAllAsync(provider, conversation);

        Assert.Equal(3, streamEvents.Count);
        Assert.Collection(
            streamEvents,
            first => Assert.Equal("a", first.Event!.Content),
            second => Assert.Equal("b", second.Event!.Content),
            third => Assert.Equal("c", third.Event!.Content));
    }

    [Fact]
    public async Task StreamAsync_WhenGivenThinkingTokensPrefix_YieldsOneTokenPerCharacter()
    {
        var provider = new EchoLlmProvider();
        var conversation = LlmConversation.Create(
            events:
            [
                TestProvider.UserTurn("thinking-tokens: xyz"),
            ]);

        var streamEvents = await ReadAllAsync(provider, conversation);

        Assert.Equal(3, streamEvents.Count);
        Assert.Collection(
            streamEvents,
            first => Assert.Equal("x", first.Event!.Thinking),
            second => Assert.Equal("y", second.Event!.Thinking),
            third => Assert.Equal("z", third.Event!.Thinking));
    }

    [Fact]
    public async Task StreamAsync_WhenGivenAnythingElse_YieldsContentEvent()
    {
        var provider = new EchoLlmProvider();
        var conversation = LlmConversation.Create(
            events:
            [
                TestProvider.UserTurn("hello"),
            ]);

        var streamEvents = await ReadAllAsync(provider, conversation);

        Assert.Single(streamEvents);
        Assert.Equal("hello", streamEvents[0].Event!.Content);
        Assert.Equal(LlmEventKinds.Turn, streamEvents[0].Event!.EventKind);
    }

    private static async Task<List<LlmStreamEvent>> ReadAllAsync(
        EchoLlmProvider provider,
        LlmConversation conversation)
    {
        var streamEvents = new List<LlmStreamEvent>();
        await foreach (var streamEvent in provider.StreamAsync(conversation))
        {
            streamEvents.Add(streamEvent);
        }

        return streamEvents;
    }
}
