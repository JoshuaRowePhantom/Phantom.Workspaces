using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatTests
{
    private static AgentChat CreateChat(params ChatResponseUpdate[] updates)
    {
        var manager = new AgentInputQueueManager(
            new ChatClientAgent(
                new TestChatClient(updates),
                new ChatClientAgentOptions { UseProvidedChatClientAsIs = true }));
        return new AgentChat(manager);
    }

    [Fact]
    public async Task EnqueueUserContents_AcceptsTextAndImageContent()
    {
        await using var chat = CreateChat();

        var image = new DataContent(new byte[] { 0x01, 0x02 }, "image/png");
        chat.EnqueueUserContents([new TextContent("hello"), image]);

        Assert.Equal(2, chat.History.Count);
        var userHistory = chat.History[0];
        Assert.Equal(ChatRole.User, userHistory.Role);
        Assert.Equal("hello[image/png]", userHistory.Text);
        Assert.Equal(2, userHistory.Contents.Count);
        Assert.IsType<TextContent>(userHistory.Contents[0]);
        Assert.IsType<DataContent>(userHistory.Contents[1]);

        var assistantPlaceholder = chat.History[1];
        Assert.Equal(ChatRole.Assistant, assistantPlaceholder.Role);
        Assert.True(assistantPlaceholder.IsInProgress);
    }

    [Fact]
    public async Task EnqueueUserMessage_AddsPendingAssistantItemImmediately()
    {
        await using var chat = CreateChat();

        chat.EnqueueUserMessage("hello");

        Assert.Equal(2, chat.History.Count);
        Assert.Equal(ChatRole.User, chat.History[0].Role);
        Assert.Equal(ChatRole.Assistant, chat.History[1].Role);
        Assert.True(chat.History[1].IsInProgress);
    }

    [Fact]
    public async Task StreamingCompletion_UpdatesAssistantPlaceholderInPlace()
    {
        await using var chat = CreateChat(
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("An")]),
            new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("swering")]),
            new ChatResponseUpdate(ChatRole.Assistant, "hello "),
            new ChatResponseUpdate(ChatRole.Assistant, "world")
            {
                FinishReason = ChatFinishReason.Stop,
            });

        chat.EnqueueUserMessage("hi");

        await Task.Delay(150);

        Assert.Equal(2, chat.History.Count);
        var assistantItem = chat.History[1];
        Assert.Equal(ChatRole.Assistant, assistantItem.Role);
        Assert.Equal("hello world", assistantItem.Text);
        Assert.Equal("Answering", assistantItem.ReasoningText);
        Assert.False(assistantItem.IsInProgress);
    }

    [Fact]
    public async Task CreateInputQueue_AddsQueueToInputQueues()
    {
        await using var chat = CreateChat();

        var created = chat.CreateInputQueue();

        Assert.Contains(created, chat.InputQueues);
        Assert.False(created.IsDefault);
        Assert.Equal(2, chat.InputQueues.Count);
    }

    [Fact]
    public async Task RemoveInputQueue_DefaultQueueCannotBeRemoved()
    {
        await using var chat = CreateChat();

        var removed = chat.RemoveInputQueue(chat.DefaultInputQueue);

        Assert.False(removed);
        Assert.Single(chat.InputQueues);
    }
}
