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
    public void TryDequeueNextImmediateOrQueued_AppliedRemoval_AdvancesRevisionAndRaisesStateChanged()
    {
        var manager = CreateManager();
        var item = Item("consumed");
        manager.Enqueue(manager.ImmediateQueue, [item]);
        var before = manager.AggregateRevision;
        var removals = 0;
        var publications = 0;
        manager.QueueStateChanged += (_, args) =>
        {
            if (args.ChangeKind == AgentInputQueueManager.QueueStateChangeKind.ItemRemoved)
            {
                removals++;
                Assert.Equal(before + 1, manager.AggregateRevision);
                Assert.Empty(args.Queue.Items);
            }
        };
        manager.QueuePublished += (_, args) =>
        {
            publications++;
            Assert.Same(item, args.Item);
        };

        Assert.True(manager.TryDequeueNextImmediateOrQueued(out var removed));
        Assert.False(manager.TryDequeueNextImmediateOrQueued(out _));

        Assert.Same(item, removed);
        Assert.Equal(before + 1, manager.AggregateRevision);
        Assert.Equal(1, removals);
        Assert.Equal(1, publications);
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
