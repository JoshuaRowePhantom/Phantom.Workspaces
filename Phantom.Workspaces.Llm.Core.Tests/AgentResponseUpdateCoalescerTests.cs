using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Tests;

public class AgentResponseUpdateCoalescerTests
{
    private static readonly TimeProvider Time = TimeProvider.System;

    private static AgentResponseUpdate Update(
        ChatRole role,
        IList<AIContent> contents,
        string? authorName = null,
        string? agentId = null,
        string? messageId = null,
        ChatFinishReason? finishReason = null)
        => new()
        {
            Role = role,
            Contents = contents,
            AuthorName = authorName,
            AgentId = agentId,
            MessageId = messageId,
            FinishReason = finishReason,
        };

    [Fact]
    public void Coalesce_AssistantTextThenToolCall_ProducesSingleMessageWithBothContents()
    {
        var updates = new[]
        {
            Update(ChatRole.Assistant, [new TextContent("Here's the situation:")]),
            Update(ChatRole.Assistant, [new FunctionCallContent("c1", "tool", null)]),
        };

        var result = AgentResponseUpdateCoalescer.Coalesce(updates, Time);

        var item = Assert.Single(result);
        Assert.Equal(ChatRole.Assistant, item.Role);
        Assert.Collection(
            item.Contents,
            c => Assert.Equal("Here's the situation:", Assert.IsType<TextContent>(c).Text),
            c => Assert.Equal("c1", Assert.IsType<FunctionCallContent>(c).CallId));
    }

    [Fact]
    public void Coalesce_MultipleAssistantTextDeltas_ConcatenatesIntoSingleTextContent()
    {
        var updates = new[]
        {
            Update(ChatRole.Assistant, [new TextContent("I've ")]),
            Update(ChatRole.Assistant, [new TextContent("recovered ")]),
            Update(ChatRole.Assistant, [new TextContent("the queue")]),
        };

        var result = AgentResponseUpdateCoalescer.Coalesce(updates, Time);

        var item = Assert.Single(result);
        var text = Assert.IsType<TextContent>(Assert.Single(item.Contents));
        Assert.Equal("I've recovered the queue", text.Text);
    }

    [Fact]
    public void Coalesce_AssistantThenToolThenAssistant_ProducesThreeMessages()
    {
        var updates = new[]
        {
            Update(ChatRole.Assistant, [new TextContent("pre")]),
            Update(ChatRole.Assistant, [new FunctionCallContent("c1", "tool", null)]),
            Update(ChatRole.Tool, [new FunctionResultContent("c1", "ok")]),
            Update(ChatRole.Assistant, [new TextContent("post")]),
        };

        var result = AgentResponseUpdateCoalescer.Coalesce(updates, Time);

        Assert.Equal(3, result.Length);

        Assert.Equal(ChatRole.Assistant, result[0].Role);
        Assert.Collection(
            result[0].Contents,
            c => Assert.Equal("pre", Assert.IsType<TextContent>(c).Text),
            c => Assert.Equal("c1", Assert.IsType<FunctionCallContent>(c).CallId));

        Assert.Equal(ChatRole.Tool, result[1].Role);
        Assert.Equal("c1", Assert.IsType<FunctionResultContent>(Assert.Single(result[1].Contents)).CallId);

        Assert.Equal(ChatRole.Assistant, result[2].Role);
        Assert.Equal("post", Assert.IsType<TextContent>(Assert.Single(result[2].Contents)).Text);
    }

    [Fact]
    public void Coalesce_MultiAgentInterleavedAssistantDeltas_DoesNotMergeAcrossAgentIds()
    {
        var updates = new[]
        {
            Update(ChatRole.Assistant, [new TextContent("root-a")], authorName: null, agentId: "root"),
            Update(ChatRole.Assistant, [new TextContent("child-a")], authorName: "child", agentId: "child"),
            Update(ChatRole.Assistant, [new TextContent("root-b")], authorName: null, agentId: "root"),
        };

        var result = AgentResponseUpdateCoalescer.Coalesce(updates, Time);

        Assert.True(result.Length >= 2);
        foreach (var item in result)
        {
            var texts = item.Contents.OfType<TextContent>().Select(t => t.Text).ToArray();
            var containsRoot = texts.Any(t => t.StartsWith("root", StringComparison.Ordinal));
            var containsChild = texts.Any(t => t.StartsWith("child", StringComparison.Ordinal));
            Assert.False(containsRoot && containsChild, "content must never be mixed between agents");
        }
    }

    [Fact]
    public void Coalesce_UpdateWithFinishReason_ClosesCurrentMessage()
    {
        var updates = new[]
        {
            Update(ChatRole.Assistant, [new TextContent("done")]),
            Update(ChatRole.Assistant, [], finishReason: ChatFinishReason.Stop),
            Update(ChatRole.Assistant, [new TextContent("next turn")]),
        };

        var result = AgentResponseUpdateCoalescer.Coalesce(updates, Time);

        Assert.Equal(2, result.Length);
        Assert.Equal("done", Assert.IsType<TextContent>(Assert.Single(result[0].Contents)).Text);
        Assert.Equal("next turn", Assert.IsType<TextContent>(Assert.Single(result[1].Contents)).Text);
    }

    [Fact]
    public void Coalesce_NonNullMessageIdsThatDiffer_SplitIntoSeparateMessages()
    {
        var updates = new[]
        {
            Update(ChatRole.Assistant, [new TextContent("first")], messageId: "m1"),
            Update(ChatRole.Assistant, [new TextContent("second")], messageId: "m2"),
        };

        var result = AgentResponseUpdateCoalescer.Coalesce(updates, Time);

        Assert.Equal(2, result.Length);
        Assert.Equal("first", Assert.IsType<TextContent>(Assert.Single(result[0].Contents)).Text);
        Assert.Equal("second", Assert.IsType<TextContent>(Assert.Single(result[1].Contents)).Text);
    }

    [Fact]
    public void Coalesce_ReasoningContentAlongsideText_PreservesBothInOrder()
    {
        var updates = new[]
        {
            Update(ChatRole.Assistant, [new TextReasoningContent("thinking")]),
            Update(ChatRole.Assistant, [new TextContent("visible")]),
            Update(ChatRole.Assistant, [new FunctionCallContent("c1", "tool", null)]),
        };

        var result = AgentResponseUpdateCoalescer.Coalesce(updates, Time);

        var item = Assert.Single(result);
        Assert.Collection(
            item.Contents,
            c => Assert.Equal("thinking", Assert.IsType<TextReasoningContent>(c).Text),
            c => Assert.Equal("visible", Assert.IsType<TextContent>(c).Text),
            c => Assert.Equal("c1", Assert.IsType<FunctionCallContent>(c).CallId));
    }

    [Fact]
    public void Coalesce_EmptyUpdatesArray_ReturnsEmptyArray()
    {
        var result = AgentResponseUpdateCoalescer.Coalesce([], Time);

        Assert.Empty(result);
        Assert.Same(Array.Empty<AgentChatHistoryItem>(), result);
    }

    [Fact]
    public void Coalesce_ToolRoleUpdate_ProducesToolRoleHistoryItem()
    {
        var updates = new[]
        {
            Update(ChatRole.Tool, [new FunctionResultContent("c1", "result")]),
        };

        var result = AgentResponseUpdateCoalescer.Coalesce(updates, Time);

        var item = Assert.Single(result);
        Assert.Equal(ChatRole.Tool, item.Role);
        Assert.Equal("c1", Assert.IsType<FunctionResultContent>(Assert.Single(item.Contents)).CallId);
    }
}
