using Microsoft.Extensions.AI;
using System.Collections.Immutable;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentInputQueueTests
{
    [Fact]
    public void Constructor_WithDefaultParameters_InitializesQueue()
    {
        var queue = new AgentInputQueue();

        Assert.Empty(queue.Items);
        Assert.Equal(0, queue.Priority);
        Assert.Equal(AgentInputQueueImmediacy.Queue, queue.Immediacy);
        Assert.Null(queue.CoalescingKey);
    }

    [Fact]
    public void Constructor_WithCustomParameters_AppliesConfiguration()
    {
        var parameters = new AgentInputQueue.Parameters
        {
            Priority = 42,
            Immediacy = AgentInputQueueImmediacy.Held,
            CoalescingKey = "mykey",
        };
        var queue = new AgentInputQueue(parameters);

        Assert.Empty(queue.Items);
        Assert.Equal(42, queue.Priority);
        Assert.Equal(AgentInputQueueImmediacy.Held, queue.Immediacy);
        Assert.Equal("mykey", queue.CoalescingKey);
    }

    [Fact]
    public void Enqueue_WithMessages_AppendsToQueue()
    {
        var queue = new AgentInputQueue();
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "hi"),
        };

        var result = queue.Enqueue(messages);

        Assert.Equal(2, queue.Items.Count);
        Assert.Equal(2, result.Count);
        Assert.Equal("hello", queue.Items[0].Text);
        Assert.Equal("hi", queue.Items[1].Text);
    }

    [Fact]
    public void Enqueue_WithEmptyList_ReturnsCurrentItems()
    {
        var queue = new AgentInputQueue();
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "hello"),
        };
        queue.Enqueue(messages);

        var result = queue.Enqueue(Array.Empty<ChatMessage>());

        Assert.Single(queue.Items);
        Assert.Single(result);
    }

    [Fact]
    public void Enqueue_MultipleCallsInSequence_MaintainsOrder()
    {
        var queue = new AgentInputQueue();

        queue.Enqueue([new ChatMessage(ChatRole.User, "first")]);
        queue.Enqueue([new ChatMessage(ChatRole.User, "second")]);
        queue.Enqueue([new ChatMessage(ChatRole.User, "third")]);

        Assert.Equal(3, queue.Items.Count);
        Assert.Equal("first", queue.Items[0].Text);
        Assert.Equal("second", queue.Items[1].Text);
        Assert.Equal("third", queue.Items[2].Text);
    }

    [Fact]
    public void Update_WithValidExpectedItems_UpdatesSuccessfully()
    {
        var queue = new AgentInputQueue();
        var oldItems = queue.Items;
        var newItems = oldItems.Add(new ChatMessage(ChatRole.User, "test"));

        var result = queue.Update(oldItems, newItems);

        Assert.True(result);
        Assert.Equal(newItems, queue.Items);
    }

    [Fact]
    public void Update_WithoutMatchingExpectedItems_DoesNotUpdate()
    {
        var queue = new AgentInputQueue();
        queue.Enqueue([new ChatMessage(ChatRole.User, "existing")]);

        var wrongExpectedItems = ImmutableList<ChatMessage>.Empty;
        var newItems = wrongExpectedItems.Add(new ChatMessage(ChatRole.User, "test"));

        var result = queue.Update(wrongExpectedItems, newItems);

        Assert.False(result);
        Assert.Single(queue.Items);
        Assert.Equal("existing", queue.Items[0].Text);
    }

    [Fact]
    public void Configure_UpdatesQueueParameters()
    {
        var queue = new AgentInputQueue(
            new AgentInputQueue.Parameters { Priority = 10 });

        var newParameters = new AgentInputQueue.Parameters
        {
            Priority = 50,
            Immediacy = AgentInputQueueImmediacy.Immediate,
            CoalescingKey = "newkey",
        };
        queue.Configure(newParameters);

        Assert.Equal(50, queue.Priority);
        Assert.Equal(AgentInputQueueImmediacy.Immediate, queue.Immediacy);
        Assert.Equal("newkey", queue.CoalescingKey);
    }

    [Fact]
    public void Enqueue_WithNullArgument_ThrowsArgumentNullException()
    {
        var queue = new AgentInputQueue();

        var ex = Assert.Throws<ArgumentNullException>(() => queue.Enqueue(null!));
        Assert.Equal("newItems", ex.ParamName);
    }

    [Fact]
    public void Update_WithNullExistingItems_ThrowsArgumentNullException()
    {
        var queue = new AgentInputQueue();
        var newItems = ImmutableList<ChatMessage>.Empty;

        var ex = Assert.Throws<ArgumentNullException>(() => queue.Update(null!, newItems));
        Assert.Equal("existingItems", ex.ParamName);
    }

    [Fact]
    public void Update_WithNullNewItems_ThrowsArgumentNullException()
    {
        var queue = new AgentInputQueue();
        var existingItems = queue.Items;

        var ex = Assert.Throws<ArgumentNullException>(() => queue.Update(existingItems, null!));
        Assert.Equal("newItems", ex.ParamName);
    }

    [Fact]
    public void Configure_WithNullParameters_ThrowsArgumentNullException()
    {
        var queue = new AgentInputQueue();

        var ex = Assert.Throws<ArgumentNullException>(() => queue.Configure(null!));
        Assert.Equal("parameters", ex.ParamName);
    }

    [Fact]
    public async Task Enqueue_ConcurrentAccess_MaintainsThreadSafety()
    {
        var queue = new AgentInputQueue();
        var tasks = new Task[10];

        for (var i = 0; i < 10; i++)
        {
            var index = i;
            tasks[i] = Task.Run(() =>
            {
                queue.Enqueue([new ChatMessage(ChatRole.User, $"message-{index}")]);
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(10, queue.Items.Count);
    }
}
