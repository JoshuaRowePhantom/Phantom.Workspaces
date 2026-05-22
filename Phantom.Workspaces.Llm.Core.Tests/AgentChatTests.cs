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

    private static AgentChat CreateBusyChat(IChatClient client)
    {
        var manager = new AgentInputQueueManager(
            new ChatClientAgent(
                client,
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

    [Fact]
    public async Task EnqueueUserMessage_ToQueuedQueue_PublishesImmediatelyWhenIdle()
    {
        await using var chat = CreateChat();
        var queue = chat.CreateInputQueue();

        chat.EnqueueUserMessage("queued later", queue);

        Assert.Equal(2, chat.History.Count);
        Assert.Equal("queued later", chat.History[0].Text);
        Assert.Empty(queue.Items);
    }

    [Fact]
    public async Task EnqueueUserMessage_ToQueuedQueue_WaitsWhileBusy()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var chat = CreateBusyChat(new BusyTestChatClient(
            started,
            release,
            new ChatResponseUpdate(ChatRole.Assistant, "working "),
            new ChatResponseUpdate(ChatRole.Assistant, "done")
            {
                FinishReason = ChatFinishReason.Stop,
            }));

        var queue = chat.CreateInputQueue();
        chat.EnqueueUserMessage("start");

        await started.Task;

        chat.EnqueueUserMessage("queued while busy", queue);

        Assert.Single(queue.Items);
        Assert.Equal(2, chat.History.Count);

        release.SetResult();
        await Task.Delay(100);

        Assert.Empty(queue.Items);
        Assert.Equal(4, chat.History.Count);
        Assert.Equal("queued while busy", chat.History[2].Text);
    }

    [Fact]
    public async Task UpdateQueueItem_PreservesImageAttachments()
    {
        await using var chat = CreateChat();
        var queue = chat.CreateInputQueue(immediacy: AgentInputQueueImmediacy.Held);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO3ZfV0AAAAASUVORK5CYII=");
        chat.EnqueueUserContents([new TextContent("hello"), new DataContent(png, "image/png")], chat.InputQueues[1]);

        var updated = chat.UpdateQueueItem(queue, 0, "edited");

        Assert.True(updated);
        Assert.Equal(2, queue.Items[0].Contents.Count);
        Assert.Equal("edited", queue.Items[0].Contents.OfType<TextContent>().Single().Text);
        Assert.IsType<DataContent>(queue.Items[0].Contents[1]);
    }

    [Fact]
    public void AgentInputQueue_RaisesChangedWhenItemsMutate()
    {
        var queue = new AgentInputQueue();
        var changedCount = 0;
        queue.Changed += (_, _) => changedCount++;

        queue.Enqueue([new ChatMessage(ChatRole.User, "hello")]);
        var items = queue.Items;
        queue.TryRemoveAt(ref items, 0);

        Assert.Equal(2, changedCount);
    }

    private sealed class BusyTestChatClient : IChatClient
    {
        private readonly TaskCompletionSource started;
        private readonly TaskCompletionSource release;
        private readonly IReadOnlyCollection<ChatResponseUpdate> updates;

        public BusyTestChatClient(
            TaskCompletionSource started,
            TaskCompletionSource release,
            params ChatResponseUpdate[] updates)
        {
            this.started = started;
            this.release = release;
            this.updates = updates;
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var content = string.Empty;
            await foreach (var update in this.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                content += update.Text;
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, content));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in this.updates.Take(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                this.started.TrySetResult();
                await Task.Yield();
            }

            await this.release.Task.WaitAsync(cancellationToken);

            foreach (var update in this.updates.Skip(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
                await Task.Yield();
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose()
        {
        }
    }
}
