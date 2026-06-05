using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentInputQueueManagerTests
{
    private static AgentInputQueueManager CreateManager()
        => new();

    private static AgentInputItem Item(string text)
        => new()
        {
            Messages = [new ChatMessage(ChatRole.User, text)],
        };

    [Fact]
    public void Enqueue_ImmediateQueue_CanBeDequeuedAsImmediate()
    {
        var manager = CreateManager();

        manager.Enqueue(
            manager.ImmediateQueue,
            [Item("immediate")]);

        Assert.True(manager.TryDequeueNextImmediate(out var input));
        Assert.Single(input.Messages);
        Assert.Equal("immediate", input.Messages[0].Text);
    }

    [Fact]
    public void TryDequeueNextImmediateOrQueued_IncludesQueued_ButImmediateOnlyDoesNot()
    {
        var manager = CreateManager();
        var queuedQueue = new AgentInputQueue(
            new AgentInputQueue.Parameters
            {
                Priority = 100,
                Immediacy = AgentInputQueueImmediacy.Queue,
            });
        manager.RegisterInputQueue(queuedQueue);
        manager.Enqueue(queuedQueue, [Item("queued")]);

        Assert.False(manager.TryDequeueNextImmediate(out _));
        Assert.True(manager.TryDequeueNextImmediateOrQueued(out var input));
        Assert.Single(input.Messages);
        Assert.Equal("queued", input.Messages[0].Text);
    }

    [Fact]
    public void TryDequeueNextImmediateOrQueued_PrefersImmediateByPriorityAndLeavesQueuedItem()
    {
        var manager = CreateManager();
        var queue = new AgentInputQueue(
            new AgentInputQueue.Parameters
            {
                Priority = 1,
                Immediacy = AgentInputQueueImmediacy.Queue,
            });
        manager.RegisterInputQueue(queue);
        manager.Enqueue(queue, [Item("queued")]);
        manager.Enqueue(manager.ImmediateQueue, [Item("immediate")]);

        Assert.True(manager.TryDequeueNextImmediateOrQueued(out var input));
        Assert.Equal("immediate", input.Messages[0].Text);
        Assert.Single(queue.Items);
    }

    [Fact]
    public void Configure_ReleasedHeldQueue_PublishesStateChangeAndAllowsDequeue()
    {
        var manager = CreateManager();
        var queue = new AgentInputQueue(
            new AgentInputQueue.Parameters
            {
                Priority = 100,
                Immediacy = AgentInputQueueImmediacy.Held,
            });
        manager.RegisterInputQueue(queue);
        manager.Enqueue(queue, [Item("queued")]);

        var configurationChanges = 0;
        manager.QueueStateChanged += (_, e) =>
        {
            if (e.ChangeKind == AgentInputQueueManager.QueueStateChangeKind.ConfigurationChanged)
            {
                configurationChanges++;
            }
        };

        Assert.False(manager.TryDequeueNextImmediateOrQueued(out _));

        queue.Configure(new AgentInputQueue.Parameters
        {
            Priority = 100,
            Immediacy = AgentInputQueueImmediacy.Queue,
        });

        Assert.True(configurationChanges > 0);
        Assert.True(manager.TryDequeueNextImmediateOrQueued(out var input));
        Assert.Equal("queued", input.Messages[0].Text);
    }
}
