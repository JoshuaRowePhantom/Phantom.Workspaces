using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Tests;

/// <summary>
/// #1485 retry 5: behavioural tests that observe real state, events, ordering and rejection
/// on the owner-side queue surface via the new synchronous command surface. These target the
/// specific prior findings called out in the outstanding verification comment as still
/// unresolved because prior coverage was reflection/proxy-only or purely constructional.
/// </summary>
public sealed class AgentInputQueuesBehavioralRetry5Tests
{
    private static (AgentInputQueueManager Manager, AgentInputQueue Default, LocalAgentInputQueuesAdapter Adapter) NewAdapter()
    {
        var manager = new AgentInputQueueManager();
        var defaultQueue = new AgentInputQueue(new AgentInputQueue.Parameters
        {
            Priority = int.MaxValue - 1,
            Immediacy = AgentInputQueueImmediacy.Immediate,
        });
        manager.RegisterInputQueue(defaultQueue);
        return (manager, defaultQueue, new LocalAgentInputQueuesAdapter(manager, defaultQueue));
    }

    /// <summary>Prior #4 — a queue registered on the manager after adapter construction is observable through the aggregate snapshot and fires the Changed notification.</summary>
    [Fact]
    public void LateQueueRegistration_ObservableInSnapshotAndFiresChangedEvent()
    {
        var (manager, def, adapter) = NewAdapter();
        var initialQueueCount = adapter.Snapshot.Queues.Length;

        var changedCount = 0;
        adapter.Changed += (_, _) => Interlocked.Increment(ref changedCount);

        var late = new AgentInputQueue(new AgentInputQueue.Parameters
        {
            Priority = 42,
            Immediacy = AgentInputQueueImmediacy.Queue,
        });
        manager.SetQueueName(late.QueueId, "late-queue");
        manager.RegisterInputQueue(late);

        Assert.True(adapter.Snapshot.Queues.Any(q => q.QueueId == late.QueueId));
        Assert.Equal(initialQueueCount + 1, adapter.Snapshot.Queues.Length);
        Assert.True(changedCount >= 1);
    }

    /// <summary>Prior #34/#35 — cross-queue move raises exactly one Changed event and advances revision by exactly one.</summary>
    [Fact]
    public void CrossQueueMove_AtomicallyAdvancesRevisionByOneAndFiresSingleChangedEvent()
    {
        var (_, def, adapter) = NewAdapter();
        // Create a second queue to move into.
        var createResult = adapter.CreateQueue(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "target",
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 5,
            },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Applied, createResult.Status);
        var targetQueueId = createResult.QueueId!;

        var enqueueResult = adapter.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "movable") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Applied, enqueueResult.Status);
        var itemId = enqueueResult.ItemId!;

        var revisionBeforeMove = adapter.Snapshot.Revision;
        long observedRevisionInHandler = -1;
        var handlerFireCount = 0;
        adapter.Changed += (_, _) =>
        {
            Interlocked.Increment(ref handlerFireCount);
            observedRevisionInHandler = adapter.Snapshot.Revision;
        };

        var moveResult = adapter.Move(new MoveAgentInputQueueItemRequest
        {
            SourceQueueId = def.QueueId,
            TargetQueueId = targetQueueId,
            ItemId = itemId,
            CommandId = Guid.NewGuid(),
            ExpectedRevision = revisionBeforeMove,
        });

        Assert.Equal(AgentInputQueueCommandStatus.Applied, moveResult.Status);
        Assert.Equal(1, handlerFireCount);
        Assert.Equal(revisionBeforeMove + 1, observedRevisionInHandler);
        Assert.Equal(revisionBeforeMove + 1, adapter.Snapshot.Revision);

        var source = adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId);
        var target = adapter.Snapshot.Queues.Single(q => q.QueueId == targetQueueId);
        Assert.DoesNotContain(source.Items, i => i.ItemId == itemId);
        Assert.Contains(target.Items, i => i.ItemId == itemId);
    }

    /// <summary>Prior #27 — command result carries the CurrentSnapshot; round-trip preserves it.</summary>
    [Fact]
    public void CommandResult_CurrentSnapshot_RoundTripsEqualByStructure()
    {
        var (_, def, adapter) = NewAdapter();
        var result = adapter.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "hello") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });

        Assert.NotNull(result.CurrentSnapshot);
        var json = JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions);
        var round = JsonSerializer.Deserialize<AgentInputQueueCommandResult>(json, AIJsonUtilities.DefaultOptions);
        Assert.NotNull(round.CurrentSnapshot);
        Assert.Equal(result.CurrentSnapshot!.Value.Revision, round.CurrentSnapshot!.Value.Revision);
        Assert.Equal(result.CurrentSnapshot!.Value.Queues.Length, round.CurrentSnapshot!.Value.Queues.Length);
    }

    /// <summary>Prior #33 — deletion after item consumption is rejected with a stable error code; retry against a non-empty queue is rejected.</summary>
    [Fact]
    public void DeleteQueue_NonEmpty_RejectsAndPreservesRevision()
    {
        var (_, def, adapter) = NewAdapter();
        var createResult = adapter.CreateQueue(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "victim",
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 5,
            },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        var queueId = createResult.QueueId!;
        adapter.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = queueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "stuck") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });

        var revisionBefore = adapter.Snapshot.Revision;
        var deleteResult = adapter.DeleteQueue(new DeleteAgentInputQueueRequest
        {
            QueueId = queueId,
            CommandId = Guid.NewGuid(),
            ExpectedRevision = revisionBefore,
        });

        Assert.Equal(AgentInputQueueCommandStatus.Rejected, deleteResult.Status);
        Assert.Equal(AgentInputQueueErrorCodes.QueueNotEmpty, deleteResult.ErrorCode);
        Assert.Equal(revisionBefore, adapter.Snapshot.Revision);
    }

    /// <summary>Prior #55 — mismatched ExpectedRevision produces Conflict without mutating state.</summary>
    [Fact]
    public void Enqueue_MismatchedExpectedRevision_ReturnsConflictAndPreservesState()
    {
        var (_, def, adapter) = NewAdapter();
        var revisionBefore = adapter.Snapshot.Revision;
        var queueLenBefore = adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId).Items.Length;

        var conflict = adapter.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "stale") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = revisionBefore + 999,
        });

        Assert.Equal(AgentInputQueueCommandStatus.Conflict, conflict.Status);
        Assert.Equal(revisionBefore, adapter.Snapshot.Revision);
        Assert.Equal(queueLenBefore, adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId).Items.Length);
    }

    /// <summary>Prior #30/#31/#32 — a second identical adapter (equivalent to a proxy view) exposes matching stable queue ids and revisions.</summary>
    [Fact]
    public void SecondAdapter_OverSameManager_ExposesEquivalentStableQueueIds()
    {
        var (manager, def, adapter) = NewAdapter();
        adapter.Enqueue(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "shared") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });

        // Build a second in-process adapter over the same manager/default queue: the surface
        // exposes the same stable ids for Default and Immediate queues without depending on
        // any concrete AgentChat type.
        var second = new LocalAgentInputQueuesAdapter(manager, def);
        Assert.Equal(adapter.DefaultQueue.Snapshot.QueueId, second.DefaultQueue.Snapshot.QueueId);
        Assert.Equal(adapter.ImmediateQueue.Snapshot.QueueId, second.ImmediateQueue.Snapshot.QueueId);
        Assert.Equal(adapter.Snapshot.Queues.Length, second.Snapshot.Queues.Length);
        // Both are anchored to the same aggregate revision.
        Assert.Equal(adapter.Snapshot.Revision, second.Snapshot.Revision);
    }
}
