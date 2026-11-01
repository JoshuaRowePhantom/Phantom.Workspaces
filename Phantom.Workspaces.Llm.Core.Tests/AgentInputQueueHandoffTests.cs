using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class AgentInputQueueHandoffTests
{
    [Fact]
    public void Handoff_PausesDispatchAndRejectsMutationWithoutChangingRevisions()
    {
        var manager = new AgentInputQueueManager();
        var queues = new AgentChatQueueManager(manager);
        using var adapter = new LocalAgentInputQueuesAdapter(manager, queues.DefaultInputQueue.Queue);
        adapter.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = adapter.DefaultQueue.Snapshot.QueueId,
            Messages = [new ChatMessage(ChatRole.User, "pending")],
            ExpectedRevision = adapter.Snapshot.Revision,
            CommandId = Guid.NewGuid(),
        });

        var checkpoint = manager.BeginRuntimeHandoff(adapter);
        Assert.False(manager.TryDequeueNextImmediateOrQueued(out _));
        var result = adapter.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = adapter.DefaultQueue.Snapshot.QueueId,
            Messages = [new ChatMessage(ChatRole.User, "must not enter")],
            ExpectedRevision = checkpoint.InputQueues.Revision,
            CommandId = Guid.NewGuid(),
        });
        Assert.Equal(AgentInputQueueCommandStatus.Rejected, result.Status);
        Assert.Equal(AgentInputQueueErrorCodes.RuntimeHandoff, result.ErrorCode);
        Assert.Throws<InvalidOperationException>(() => manager.Enqueue(
            manager.ImmediateQueue,
            [new AgentInputItem { Messages = [new ChatMessage(ChatRole.User, "legacy")] }]));
        Assert.Throws<InvalidOperationException>(() => manager.ImmediateQueue.Enqueue(
            [new AgentInputItem { Messages = [new ChatMessage(ChatRole.User, "raw")] }]));
        Assert.Throws<InvalidOperationException>(() => manager.ImmediateQueue.Configure(
            new AgentInputQueue.Parameters { Priority = 1 }));
        Assert.Equal(checkpoint.InputQueues.Revision, adapter.Snapshot.Revision);
        Assert.Equal("pending", Assert.Single(adapter.DefaultQueue.Snapshot.Items).Messages[0].Text);
    }

    [Fact]
    public void Handoff_DurableRoundTripRestoresIdsConfigurationAndRevisionsBeforeDispatch()
    {
        var manager = new AgentInputQueueManager();
        var queues = new AgentChatQueueManager(manager);
        using var adapter = new LocalAgentInputQueuesAdapter(manager, queues.DefaultInputQueue.Queue);
        var held = queues.CreateInputQueue("retain", AgentInputQueueImmediacy.Held);
        adapter.Configure(new ConfigureAgentInputQueueRequest
        {
            QueueId = adapter.DefaultQueue.Snapshot.QueueId,
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "renamed default",
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 50,
                CoalescingKey = "keep-key",
            },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        manager.Enqueue(held.Queue,
            [new AgentInputItem { Messages = [new ChatMessage(ChatRole.User, "retained")] }]);
        manager.Enqueue(manager.ImmediateQueue,
            [new AgentInputItem { Messages = [new ChatMessage(ChatRole.User, "next")] }]);
        var checkpoint = manager.BeginRuntimeHandoff(adapter);
        var durable = JsonSerializer.Deserialize<AgentRuntimeQueueCheckpoint>(
            JsonSerializer.Serialize(checkpoint, AIJsonUtilities.DefaultOptions),
            AIJsonUtilities.DefaultOptions)!;

        var replacement = new AgentInputQueueManager(durable);
        var replacementQueues = new AgentChatQueueManager(replacement);
        using var replacementAdapter = new LocalAgentInputQueuesAdapter(
            replacement, replacementQueues.DefaultInputQueue.Queue);
        Assert.Equal(JsonSerializer.Serialize(checkpoint.InputQueues, AIJsonUtilities.DefaultOptions),
            JsonSerializer.Serialize(replacementAdapter.Snapshot, AIJsonUtilities.DefaultOptions));
        Assert.False(replacement.TryDequeueNextImmediateOrQueued(out _));
        replacement.ResumeAfterRuntimeHandoff();
        Assert.True(replacement.TryDequeueNextImmediateOrQueued(out var pending));
        Assert.Equal("next", pending.Messages[0].Text);
        Assert.False(replacement.TryDequeueNextImmediateOrQueued(out _));
        Assert.Equal("retained", Assert.Single(replacementAdapter.Snapshot.Queues.Single(
            queue => queue.Name == "retain").Items).Messages[0].Text);
    }

    [Fact]
    public void Handoff_RejectsForeignQueueAggregateWithoutPausingDispatch()
    {
        var owner = new AgentInputQueueManager();
        var foreign = new AgentInputQueueManager();
        var foreignQueues = new AgentChatQueueManager(foreign);
        using var foreignAdapter = new LocalAgentInputQueuesAdapter(
            foreign, foreignQueues.DefaultInputQueue.Queue);
        Assert.Throws<InvalidOperationException>(() => owner.BeginRuntimeHandoff(foreignAdapter));
        owner.Enqueue(owner.ImmediateQueue,
            [new AgentInputItem { Messages = [new ChatMessage(ChatRole.User, "still dispatchable")] }]);
        Assert.True(owner.TryDequeueNextImmediate(out _));
    }

    [Fact]
    public void Handoff_DoesNotReplayDequeuedWorkAndCancellationCanResumeOriginal()
    {
        var manager = new AgentInputQueueManager();
        var queues = new AgentChatQueueManager(manager);
        using var adapter = new LocalAgentInputQueuesAdapter(manager, queues.DefaultInputQueue.Queue);
        manager.Enqueue(manager.ImmediateQueue,
            [new AgentInputItem { Messages = [new ChatMessage(ChatRole.User, "already dispatched")] }]);
        Assert.True(manager.TryDequeueNextImmediate(out _));
        manager.Enqueue(manager.ImmediateQueue,
            [new AgentInputItem { Messages = [new ChatMessage(ChatRole.User, "unsent")] }]);

        var checkpoint = manager.BeginRuntimeHandoff(adapter);
        Assert.True(checkpoint.HasInterruptedWork);
        Assert.Equal("unsent", Assert.Single(checkpoint.InputQueues.Queues.Single(
            queue => queue.IsImmediate).Items).Messages[0].Text);
        manager.ResumeAfterRuntimeHandoff();
        Assert.True(manager.TryDequeueNextImmediate(out var pending));
        Assert.Equal("unsent", pending.Messages[0].Text);
    }
}
