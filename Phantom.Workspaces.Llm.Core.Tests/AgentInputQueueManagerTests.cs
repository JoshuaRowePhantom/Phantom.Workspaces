using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentInputQueueManagerTests
{
    private static AgentInputQueueManager CreateManager(params ChatResponseUpdate[] updates)
        => new AgentInputQueueManager(
            new ChatClientAgent(
                new TestChatClient(updates),
                new ChatClientAgentOptions { UseProvidedChatClientAsIs = true }));

    [Fact]
    public async Task Enqueue_ImmediateQueue_IsProcessedByAgent()
    {
        var manager = CreateManager(new ChatResponseUpdate(ChatRole.Assistant, "response"));

        await using var updates = manager.Process().GetAsyncEnumerator();
        manager.Enqueue(
            manager.ImmediateQueue,
            [new ChatMessage(ChatRole.User, "immediate")]);

        Assert.True(await updates.MoveNextAsync());
        Assert.Equal("response", updates.Current.Text);

        manager.Complete();
    }

    [Fact]
    public async Task ServiceQueues_HonorsImmediacyRules()
    {
        var manager = CreateManager(new ChatResponseUpdate(ChatRole.Assistant, "response"));
        var queuedQueue = new AgentInputQueue(
            new AgentInputQueue.Parameters
            {
                Priority = 100,
                Immediacy = AgentInputQueueImmediacy.Queue,
            });
        var heldQueue = new AgentInputQueue(
            new AgentInputQueue.Parameters
            {
                Priority = 1000,
                Immediacy = AgentInputQueueImmediacy.Held,
            });
        manager.RegisterInputQueue(queuedQueue);
        manager.RegisterInputQueue(heldQueue);

        manager.Enqueue(queuedQueue, [new ChatMessage(ChatRole.User, "queued")]);
        manager.Enqueue(heldQueue, [new ChatMessage(ChatRole.System, "held")]);
        Assert.Empty(queuedQueue.Items);
        Assert.Single(heldQueue.Items);
        Assert.Equal(0, manager.ServiceQueues(modelTurnIncludedToolCalls: true));
        Assert.Equal(0, manager.ServiceQueues(modelTurnIncludedToolCalls: false));

        await using var updates = manager.Process().GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync());

        manager.Complete();
    }

    [Fact]
    public async Task Interrupt_PublishesInterruptInputAndDrainsQueue()
    {
        var manager = CreateManager(new ChatResponseUpdate(ChatRole.Assistant, "response"));
        var queue = new AgentInputQueue(
            new AgentInputQueue.Parameters
            {
                Priority = 1,
                Immediacy = AgentInputQueueImmediacy.Held,
            });
        manager.RegisterInputQueue(queue);
        queue.Enqueue([new ChatMessage(ChatRole.User, "interrupt me")]);

        await using var updates = manager.Process().GetAsyncEnumerator();

        var interruptedItems = manager.Interrupt(queue);

        Assert.Equal(1, interruptedItems);
        Assert.Empty(queue.Items);
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal("response", updates.Current.Text);

        manager.Complete();
    }
}
