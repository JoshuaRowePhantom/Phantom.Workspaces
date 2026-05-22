using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Echo;

namespace Phantom.Workspaces.Llm.Core.Tests;

public class ProviderReasoningTests
{
    [Fact]
    public async Task EchoChatClient_ThinkingTokens_EmitsReasoningContent()
    {
        var client = new EchoChatClient();
        var updates = await CollectUpdatesAsync(client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "thinking-tokens: abc")]));

        Assert.Equal(3, updates.Count);
        Assert.All(updates, update => Assert.IsType<TextReasoningContent>(Assert.Single(update.Contents)));
        Assert.Equal("abc", string.Concat(updates.Select(update => Assert.Single(update.Contents)).Cast<TextReasoningContent>().Select(content => content.Text)));
    }

    [Fact]
    public async Task EchoChatClient_ToolUse_EmitsReasoningBeforeToolResult()
    {
        var client = new EchoChatClient();
        var updates = await CollectUpdatesAsync(client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "tool_use: lookup cat")]));

        Assert.Equal(2, updates.Count);
        var firstContent = Assert.Single(updates[0].Contents);
        Assert.IsType<TextReasoningContent>(firstContent);
        Assert.Contains("Calling tool lookup", ((TextReasoningContent)firstContent).Text, StringComparison.Ordinal);

        Assert.Equal("[tool: lookup cat]", updates[1].Text);
    }

    [Fact]
    public async Task TestProviderChatClient_ReasoningTokens_EmitsReasoningContent()
    {
        var client = new TestProviderChatClient();
        var updates = await CollectUpdatesAsync(client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "reasoning-tokens: ok")]));

        Assert.Equal(2, updates.Count);
        Assert.All(updates, update => Assert.IsType<TextReasoningContent>(Assert.Single(update.Contents)));
        Assert.Equal("ok", string.Concat(updates.Select(update => Assert.Single(update.Contents)).Cast<TextReasoningContent>().Select(content => content.Text)));
    }

    private static async Task<List<ChatResponseUpdate>> CollectUpdatesAsync(IAsyncEnumerable<ChatResponseUpdate> updates)
    {
        var collected = new List<ChatResponseUpdate>();
        await foreach (var update in updates)
        {
            collected.Add(update);
        }

        return collected;
    }
}
