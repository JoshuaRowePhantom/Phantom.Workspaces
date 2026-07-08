using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Xunit;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class SubAgentChatClientTests
{
    private static SubAgentChatClient CreateClient() =>
        new("agent-1", "Test Agent");

    private static ChatResponseUpdate TextUpdate(string text) =>
        new() { Role = ChatRole.Assistant, Contents = [new TextContent(text)] };

    private static ChatResponseUpdate ToolCallUpdate(string callId, string toolName) =>
        new() { Role = ChatRole.Assistant, Contents = [new FunctionCallContent(callId, toolName)] };

    [Fact]
    public async Task CompleteStreamingAsync_YieldsAllPushedUpdates()
    {
        var sut = CreateClient();
        var update1 = TextUpdate("hello");
        var update2 = TextUpdate("world");

        sut.Push(update1);
        sut.Push(update2);
        sut.Complete();

        var results = new List<ChatResponseUpdate>();
        await foreach (var u in sut.GetStreamingResponseAsync([]))
            results.Add(u);

        Assert.Equal([update1, update2], results);
    }

    [Fact]
    public async Task CompleteStreamingAsync_Completes_WhenComplete_Called()
    {
        var sut = CreateClient();
        sut.Complete();

        var count = 0;
        await foreach (var _ in sut.GetStreamingResponseAsync([]))
            count++;

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task CompleteStreamingAsync_Throws_WhenFail_Called()
    {
        var sut = CreateClient();
        var ex = new InvalidOperationException("boom");
        sut.Fail(ex);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in sut.GetStreamingResponseAsync([]))
            {
            }
        });

        Assert.Same(ex, thrown);
    }

    [Fact]
    public void CompleteAsync_Throws_NotSupportedException()
    {
        var sut = CreateClient();
        var ex = Record.Exception(() => { _ = sut.GetResponseAsync([]); });
        Assert.IsType<NotSupportedException>(ex);
    }

    [Fact]
    public void AcceptsUserInput_False_ViaIHostedAgentChatClient()
    {
        var sut = CreateClient();
        Assert.IsAssignableFrom<IHostedAgentChatClient>(sut);
    }
}
