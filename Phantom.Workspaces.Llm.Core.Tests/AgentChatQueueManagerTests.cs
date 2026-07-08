using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentChatQueueManagerTests
{
    private static AgentInputItem Item(string text)
        => new()
        {
            Messages = [new ChatMessage(ChatRole.User, text)],
        };

    [Fact]
    public void RemoveQueueItem_ItemNotInQueue_ReturnsFalse()
    {
        var inputQueueManager = new AgentInputQueueManager();
        var queueManager = new AgentChatQueueManager(inputQueueManager);
        var queue = queueManager.CreateInputQueue();

        var item1 = Item("test1");
        var item2 = Item("test2");
        queue.Queue.Enqueue([item1]);

        var result = queueManager.RemoveQueueItem(queue, item2);

        Assert.False(result);
        Assert.Single(queue.Queue.Items);
        Assert.Same(item1, queue.Queue.Items[0]);
    }

    [Fact]
    public void RemoveQueueItem_ItemPresent_ReturnsTrue()
    {
        var inputQueueManager = new AgentInputQueueManager();
        var queueManager = new AgentChatQueueManager(inputQueueManager);
        var queue = queueManager.CreateInputQueue();

        var item1 = Item("test1");
        var item2 = Item("test2");
        queue.Queue.Enqueue([item1, item2]);

        var result = queueManager.RemoveQueueItem(queue, item1);

        Assert.True(result);
        Assert.Single(queue.Queue.Items);
        Assert.Same(item2, queue.Queue.Items[0]);
    }

    [Fact]
    public async Task RemoveQueueItem_ConcurrentRemove_HandlesCorrectly()
    {
        var inputQueueManager = new AgentInputQueueManager();
        var queueManager = new AgentChatQueueManager(inputQueueManager);
        var queue = queueManager.CreateInputQueue();

        var item1 = Item("test1");
        var item2 = Item("test2");
        queue.Queue.Enqueue([item1, item2]);

        var task1 = Task.Run(() => queueManager.RemoveQueueItem(queue, item1));
        var task2 = Task.Run(() => queueManager.RemoveQueueItem(queue, item1));

        var results = await Task.WhenAll(task1, task2);

        var result1 = results[0];
        var result2 = results[1];

        Assert.True(result1 || result2);
        Assert.False(result1 && result2);
        Assert.Single(queue.Queue.Items);
        Assert.Same(item2, queue.Queue.Items[0]);
    }

    [Fact]
    public void UpdateQueueItem_ItemNotInQueue_ReturnsFalse()
    {
        var inputQueueManager = new AgentInputQueueManager();
        var queueManager = new AgentChatQueueManager(inputQueueManager);
        var queue = queueManager.CreateInputQueue();

        var item1 = Item("test1");
        var item2 = Item("test2");
        queue.Queue.Enqueue([item1]);

        var result = queueManager.UpdateQueueItem(queue, item2, "updated");

        Assert.False(result);
        Assert.Single(queue.Queue.Items);
        Assert.Same(item1, queue.Queue.Items[0]);
    }

    [Fact]
    public void UpdateQueueItem_ItemPresent_ReturnsTrue()
    {
        var inputQueueManager = new AgentInputQueueManager();
        var queueManager = new AgentChatQueueManager(inputQueueManager);
        var queue = queueManager.CreateInputQueue();

        var item1 = Item("test1");
        queue.Queue.Enqueue([item1]);

        var result = queueManager.UpdateQueueItem(queue, item1, "updated");

        Assert.True(result);
        Assert.Single(queue.Queue.Items);
        Assert.NotSame(item1, queue.Queue.Items[0]);
        var content = queue.Queue.Items[0].Messages[0].Contents.OfType<TextContent>().FirstOrDefault();
        Assert.NotNull(content);
        Assert.Equal("updated", content.Text);
    }

    [Fact]
    public async Task UpdateQueueItem_ConcurrentRemove_HandlesCorrectly()
    {
        var inputQueueManager = new AgentInputQueueManager();
        var queueManager = new AgentChatQueueManager(inputQueueManager);
        var queue = queueManager.CreateInputQueue();

        var item1 = Item("test1");
        queue.Queue.Enqueue([item1]);

        var task1 = Task.Run(() => queueManager.UpdateQueueItem(queue, item1, "updated"));
        var task2 = Task.Run(() => queueManager.RemoveQueueItem(queue, item1));

        var results = await Task.WhenAll(task1, task2);

        var updateResult = results[0];
        var removeResult = results[1];

        Assert.True(updateResult || removeResult);
        if (removeResult && !updateResult)
        {
            Assert.Empty(queue.Queue.Items);
        }
        else if (updateResult)
        {
            Assert.Single(queue.Queue.Items);
            var content = queue.Queue.Items[0].Messages[0].Contents.OfType<TextContent>().FirstOrDefault();
            Assert.NotNull(content);
        }
    }

    [Fact]
    public void RemoveQueueItemContent_ItemNotInQueue_ReturnsFalse()
    {
        var inputQueueManager = new AgentInputQueueManager();
        var queueManager = new AgentChatQueueManager(inputQueueManager);
        var queue = queueManager.CreateInputQueue();

        var item1 = Item("test1");
        var item2 = Item("test2");
        queue.Queue.Enqueue([item1]);

        var result = queueManager.RemoveQueueItemContent(queue, item2, 0);

        Assert.False(result);
        Assert.Single(queue.Queue.Items);
        Assert.Same(item1, queue.Queue.Items[0]);
    }

    [Fact]
    public void RemoveQueueItemContent_ItemPresent_ReturnsTrue()
    {
        var inputQueueManager = new AgentInputQueueManager();
        var queueManager = new AgentChatQueueManager(inputQueueManager);
        var queue = queueManager.CreateInputQueue();

        var item1 = new AgentInputItem
        {
            Messages = [new ChatMessage(ChatRole.User, [new TextContent("text1"), new TextContent("text2")])],
        };
        queue.Queue.Enqueue([item1]);

        var result = queueManager.RemoveQueueItemContent(queue, item1, 0);

        Assert.True(result);
        Assert.Single(queue.Queue.Items);
        Assert.NotSame(item1, queue.Queue.Items[0]);
        var contents = queue.Queue.Items[0].Messages[0].Contents.OfType<TextContent>().ToList();
        Assert.Single(contents);
        Assert.Equal("text2", contents[0].Text);
    }

    [Fact]
    public async Task RemoveQueueItemContent_ConcurrentRemove_HandlesCorrectly()
    {
        var inputQueueManager = new AgentInputQueueManager();
        var queueManager = new AgentChatQueueManager(inputQueueManager);
        var queue = queueManager.CreateInputQueue();

        var item1 = new AgentInputItem
        {
            Messages = [new ChatMessage(ChatRole.User, [new TextContent("text1"), new TextContent("text2")])],
        };
        queue.Queue.Enqueue([item1]);

        var task1 = Task.Run(() => queueManager.RemoveQueueItemContent(queue, item1, 0));
        var task2 = Task.Run(() => queueManager.RemoveQueueItem(queue, item1));

        var results = await Task.WhenAll(task1, task2);

        var removeContentResult = results[0];
        var removeResult = results[1];

        Assert.True(removeContentResult || removeResult);
        if (removeResult && !removeContentResult)
        {
            Assert.Empty(queue.Queue.Items);
        }
        else if (removeContentResult)
        {
            Assert.Single(queue.Queue.Items);
            var contents = queue.Queue.Items[0].Messages[0].Contents.OfType<TextContent>().ToList();
            Assert.Single(contents);
        }
    }
}
