namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentInputQueueManagerTests
{
    [Fact]
    public async Task Enqueue_ImmediateQueue_IsProcessedByAgentSessionProcess()
    {
        var session = AgentSession.Create(
            LlmSessionBuilder.Create().Build(),
            AgentExecutionEnvironmentDispatcher.Empty,
            new TestProvider());
        var manager = new AgentInputQueueManager(session);

        await using var updates = manager.Process().GetAsyncEnumerator();
        manager.Enqueue(
            manager.ImmediateQueue,
            [
                TestProvider.UserTurn("immediate"),
            ]);

        Assert.True(await updates.MoveNextAsync());
        Assert.Equal("immediate", updates.Current.LlmSession.Conversations[^1].Events[^1].Content);

        manager.Complete();
    }

    [Fact]
    public async Task ServiceQueues_HonorsImmediacyRules()
    {
        var session = AgentSession.Create(
            LlmSessionBuilder.Create().Build(),
            AgentExecutionEnvironmentDispatcher.Empty,
            new TestProvider());
        var manager = new AgentInputQueueManager(session);
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

        await using var updates = manager.Process().GetAsyncEnumerator();

        manager.Enqueue(queuedQueue, [TestProvider.UserTurn("queued")]);
        manager.Enqueue(heldQueue, [TestProvider.SystemTurn("held")]);

        var publishedWithToolCall = manager.ServiceQueues(modelTurnIncludedToolCalls: true);
        Assert.Equal(0, publishedWithToolCall);

        var publishedWithoutToolCall = manager.ServiceQueues(modelTurnIncludedToolCalls: false);
        Assert.Equal(1, publishedWithoutToolCall);

        Assert.True(await updates.MoveNextAsync());
        Assert.Equal("queued", updates.Current.LlmSession.Conversations[^1].Events[^1].Content);
        Assert.Single(heldQueue.Items);

        manager.Complete();
    }

    [Fact]
    public async Task Interrupt_PublishesInterruptInputAndDrainsQueue()
    {
        var session = AgentSession.Create(
            LlmSessionBuilder.Create().Build(),
            AgentExecutionEnvironmentDispatcher.Empty,
            new TestProvider());
        var manager = new AgentInputQueueManager(session);
        var queue = new AgentInputQueue(
            new AgentInputQueue.Parameters
            {
                Priority = 1,
                Immediacy = AgentInputQueueImmediacy.Queue,
            });
        manager.RegisterInputQueue(queue);
        queue.Enqueue([TestProvider.UserTurn("interrupt me")]);

        await using var updates = manager.Process().GetAsyncEnumerator();

        var interruptedItems = manager.Interrupt(queue);

        Assert.Equal(1, interruptedItems);
        Assert.Empty(queue.Items);
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal("interrupt me", updates.Current.LlmSession.Conversations[^1].Events[^1].Content);

        manager.Complete();
    }
}
