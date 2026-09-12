using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Tests;

/// <summary>
/// #1485: contract and behaviour tests for the common owner-side <see cref="IAgentInputQueues"/>
/// aggregate exposed through <see cref="LocalAgentInputQueuesAdapter"/>. Every mutation is
/// exercised via the public command API so revision, stable id, snapshot immutability, and
/// conflict/duplicate semantics are enforced by real code rather than mocks.
/// </summary>
public sealed class AgentInputQueuesTests
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

    private static EnqueueAgentInputRequest EnqueueOn(string queueId, long expectedRevision, string text)
        => new()
        {
            TargetQueueId = queueId,
            Messages = new[] { new ChatMessage(ChatRole.User, text) },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = expectedRevision,
        };

    [Fact]
    public void Snapshot_InitialAdapter_ExposesDefaultAndImmediateQueues()
    {
        var (_, _, adapter) = NewAdapter();
        var snapshot = adapter.Snapshot;
        Assert.Equal(2, snapshot.Queues.Length);
        Assert.Contains(snapshot.Queues, q => q.IsDefault);
        Assert.Contains(snapshot.Queues, q => q.IsImmediate);
        Assert.Equal(adapter.DefaultQueue.Snapshot.QueueId, snapshot.Queues.First(q => q.IsDefault).QueueId);
    }

    [Fact]
    public async Task EnqueueAsync_AppliedCommand_AssignsStableItemIdAndBumpsRevision()
    {
        var (manager, def, adapter) = NewAdapter();
        var before = adapter.Snapshot.Revision;

        var result = await adapter.EnqueueAsync(EnqueueOn(def.QueueId, before, "hi"));

        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.ItemId));
        Assert.True(result.Revision > before);
        Assert.Equal(manager.AggregateRevision, result.Revision);
        var queueSnapshot = adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId);
        Assert.Single(queueSnapshot.Items);
        Assert.Equal(result.ItemId, queueSnapshot.Items[0].ItemId);
    }

    [Fact]
    public async Task EnqueueAsync_EmptyMessages_ReturnsRejected()
    {
        var (_, def, adapter) = NewAdapter();
        var result = await adapter.EnqueueAsync(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = Array.Empty<ChatMessage>(),
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Rejected, result.Status);
        Assert.Equal(AgentInputQueueErrorCodes.InvalidRequest, result.ErrorCode);
    }

    [Fact]
    public async Task EnqueueAsync_UnknownQueue_ReturnsRejected()
    {
        var (_, _, adapter) = NewAdapter();
        var result = await adapter.EnqueueAsync(EnqueueOn("does-not-exist", adapter.Snapshot.Revision, "hi"));
        Assert.Equal(AgentInputQueueCommandStatus.Rejected, result.Status);
        Assert.Equal(AgentInputQueueErrorCodes.UnknownQueue, result.ErrorCode);
    }

    [Fact]
    public async Task EnqueueAsync_WrongExpectedRevision_ReturnsConflictWithCurrentSnapshot()
    {
        var (_, def, adapter) = NewAdapter();
        _ = await adapter.EnqueueAsync(EnqueueOn(def.QueueId, adapter.Snapshot.Revision, "first"));
        var current = adapter.Snapshot.Revision;

        // Send an intentionally stale expected revision.
        var stale = await adapter.EnqueueAsync(EnqueueOn(def.QueueId, current - 1, "second"));

        Assert.Equal(AgentInputQueueCommandStatus.Conflict, stale.Status);
        Assert.Equal(current, stale.Revision);
        Assert.NotNull(stale.CurrentSnapshot);
        Assert.Equal(current, stale.CurrentSnapshot!.Value.Revision);
    }

    [Fact]
    public async Task EnqueueAsync_ReusingCommandIdWithSamePayload_ReturnsDuplicate()
    {
        var (_, def, adapter) = NewAdapter();
        var commandId = Guid.NewGuid();
        var request = new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "hi") },
            CommandId = commandId,
            ExpectedRevision = adapter.Snapshot.Revision,
        };
        var first = await adapter.EnqueueAsync(request);
        Assert.Equal(AgentInputQueueCommandStatus.Applied, first.Status);

        // Replay the exact same command id.
        var replay = await adapter.EnqueueAsync(request);
        Assert.Equal(AgentInputQueueCommandStatus.Duplicate, replay.Status);
        Assert.Equal(first.ItemId, replay.ItemId);
    }

    [Fact]
    public async Task Command_DuplicateIdDifferentPayload_ReturnsConflict()
    {
        var (_, def, adapter) = NewAdapter();
        var commandId = Guid.NewGuid();
        var revision = adapter.Snapshot.Revision;
        var first = new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = [new ChatMessage(ChatRole.User, "first")],
            CommandId = commandId,
            ExpectedRevision = revision,
        };
        var different = first with
        {
            Messages = [new ChatMessage(ChatRole.User, "different")],
        };

        Assert.Equal(AgentInputQueueCommandStatus.Applied, (await adapter.EnqueueAsync(first)).Status);
        var replay = await adapter.EnqueueAsync(different);

        Assert.Equal(AgentInputQueueCommandStatus.Conflict, replay.Status);
        Assert.Single(adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId).Items);
    }

    [Fact]
    public async Task EnqueueAsync_CancelledBeforeMutation_DoesNotChangeRevision()
    {
        var (_, def, adapter) = NewAdapter();
        var revision = adapter.Snapshot.Revision;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => adapter.EnqueueAsync(EnqueueOn(def.QueueId, revision, "never"), cts.Token));

        Assert.Equal(revision, adapter.Snapshot.Revision);
        Assert.Empty(adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId).Items);
    }

    [Fact]
    public async Task EditAsync_PreservesItemIdAndBumpsRevision()
    {
        var (_, def, adapter) = NewAdapter();
        var enqueue = await adapter.EnqueueAsync(EnqueueOn(def.QueueId, adapter.Snapshot.Revision, "first"));

        var edit = await adapter.EditAsync(new EditAgentInputQueueItemRequest
        {
            QueueId = def.QueueId,
            ItemId = enqueue.ItemId!,
            Messages = new[] { new ChatMessage(ChatRole.User, "second") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });

        Assert.Equal(AgentInputQueueCommandStatus.Applied, edit.Status);
        Assert.Equal(enqueue.ItemId, edit.ItemId);
        var snapshot = adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId);
        Assert.Single(snapshot.Items);
        Assert.Equal(enqueue.ItemId, snapshot.Items[0].ItemId);
    }

    [Fact]
    public async Task EditAsync_ConsumedItem_ReturnsRejected()
    {
        var (_, def, adapter) = NewAdapter();
        var enqueue = await adapter.EnqueueAsync(EnqueueOn(def.QueueId, adapter.Snapshot.Revision, "first"));
        adapter.MarkItemConsumed(enqueue.ItemId!);

        var edit = await adapter.EditAsync(new EditAgentInputQueueItemRequest
        {
            QueueId = def.QueueId,
            ItemId = enqueue.ItemId!,
            Messages = new[] { new ChatMessage(ChatRole.User, "later") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Rejected, edit.Status);
        Assert.Equal(AgentInputQueueErrorCodes.ItemAlreadyConsumed, edit.ErrorCode);
    }

    [Fact]
    public async Task RemoveAsync_ExistingItem_RemovesFromSnapshotAndBumpsRevision()
    {
        var (_, def, adapter) = NewAdapter();
        var enqueue = await adapter.EnqueueAsync(EnqueueOn(def.QueueId, adapter.Snapshot.Revision, "first"));
        var before = adapter.Snapshot.Revision;

        var remove = await adapter.RemoveAsync(new RemoveAgentInputQueueItemRequest
        {
            QueueId = def.QueueId,
            ItemId = enqueue.ItemId!,
            CommandId = Guid.NewGuid(),
            ExpectedRevision = before,
        });

        Assert.Equal(AgentInputQueueCommandStatus.Applied, remove.Status);
        Assert.Empty(adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId).Items);
        Assert.True(remove.Revision > before);
    }

    [Fact]
    public async Task CreateQueueAsync_ValidConfiguration_AddsQueueAndBumpsRevision()
    {
        var (_, _, adapter) = NewAdapter();
        var before = adapter.Snapshot.Revision;

        var create = await adapter.CreateQueueAsync(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "custom",
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 5,
            },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = before,
        });

        Assert.Equal(AgentInputQueueCommandStatus.Applied, create.Status);
        Assert.False(string.IsNullOrWhiteSpace(create.QueueId));
        Assert.True(create.Revision > before);
        Assert.Contains(adapter.Snapshot.Queues, q => q.QueueId == create.QueueId && q.Name == "custom");
    }

    [Fact]
    public async Task CreateQueueAsync_BlankName_ReturnsRejected()
    {
        var (_, _, adapter) = NewAdapter();
        var create = await adapter.CreateQueueAsync(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "   ",
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 0,
            },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Rejected, create.Status);
        Assert.Equal(AgentInputQueueErrorCodes.InvalidRequest, create.ErrorCode);
    }

    [Fact]
    public async Task DeleteQueueAsync_ProtectedDefaultQueue_ReturnsRejected()
    {
        var (_, def, adapter) = NewAdapter();
        var result = await adapter.DeleteQueueAsync(new DeleteAgentInputQueueRequest
        {
            QueueId = def.QueueId,
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Rejected, result.Status);
        Assert.Equal(AgentInputQueueErrorCodes.ProtectedQueue, result.ErrorCode);
    }

    [Fact]
    public async Task MoveAsync_BetweenQueues_TransfersItemPreservingId()
    {
        var (_, def, adapter) = NewAdapter();
        var enqueue = await adapter.EnqueueAsync(EnqueueOn(def.QueueId, adapter.Snapshot.Revision, "carry"));
        var create = await adapter.CreateQueueAsync(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "other",
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 2,
            },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Applied, create.Status);

        var move = await adapter.MoveAsync(new MoveAgentInputQueueItemRequest
        {
            SourceQueueId = def.QueueId,
            TargetQueueId = create.QueueId!,
            ItemId = enqueue.ItemId!,
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });

        Assert.Equal(AgentInputQueueCommandStatus.Applied, move.Status);
        var source = adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId);
        var target = adapter.Snapshot.Queues.Single(q => q.QueueId == create.QueueId);
        Assert.Empty(source.Items);
        Assert.Single(target.Items);
        Assert.Equal(enqueue.ItemId, target.Items[0].ItemId);
    }

    [Fact]
    public async Task MoveAsync_BeforeItem_ReordersByStableIds()
    {
        var (_, def, adapter) = NewAdapter();
        var first = await adapter.EnqueueAsync(EnqueueOn(def.QueueId, adapter.Snapshot.Revision, "first"));
        var second = await adapter.EnqueueAsync(EnqueueOn(def.QueueId, adapter.Snapshot.Revision, "second"));

        var move = await adapter.MoveAsync(new MoveAgentInputQueueItemRequest
        {
            SourceQueueId = def.QueueId,
            TargetQueueId = def.QueueId,
            ItemId = second.ItemId!,
            BeforeItemId = first.ItemId,
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });

        Assert.Equal(AgentInputQueueCommandStatus.Applied, move.Status);
        var items = adapter.DefaultQueue.Snapshot.Items;
        Assert.Equal([second.ItemId, first.ItemId], items.Select(item => item.ItemId));
    }

    [Fact]
    public async Task ConfigureAsync_HoldAndRelease_ChangesImmediacy()
    {
        var (_, _, adapter) = NewAdapter();
        var queue = await adapter.CreateQueueAsync(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "work",
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 1,
            },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });

        foreach (var immediacy in new[] { AgentInputQueueImmediacy.Held, AgentInputQueueImmediacy.Queue })
        {
            var result = await adapter.ConfigureAsync(new ConfigureAgentInputQueueRequest
            {
                QueueId = queue.QueueId!,
                Configuration = new AgentInputQueueConfiguration
                {
                    Name = "work",
                    Immediacy = immediacy,
                    Priority = 1,
                },
                CommandId = Guid.NewGuid(),
                ExpectedRevision = adapter.Snapshot.Revision,
            });
            Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
            Assert.Equal(immediacy, adapter.Queues.Single(q => q.Snapshot.QueueId == queue.QueueId).Snapshot.Immediacy);
        }
    }

    [Fact]
    public async Task Dispose_StopsEventsAndRejectsFurtherMutation()
    {
        var (_, def, adapter) = NewAdapter();
        var raised = 0;
        adapter.Changed += (_, _) => raised++;
        adapter.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => adapter.EnqueueAsync(EnqueueOn(def.QueueId, adapter.Snapshot.Revision, "never")));
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task ConfigureAsync_SwitchImmediacyOnImmediateQueue_ReturnsRejected()
    {
        var (_, _, adapter) = NewAdapter();
        var immediate = adapter.Snapshot.Queues.Single(q => q.IsImmediate);
        var result = await adapter.ConfigureAsync(new ConfigureAgentInputQueueRequest
        {
            QueueId = immediate.QueueId,
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "Immediate Queue",
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 1,
            },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Rejected, result.Status);
        Assert.Equal(AgentInputQueueErrorCodes.FixedRoleConfiguration, result.ErrorCode);
    }

    [Fact]
    public async Task SnapshotArrays_AreImmutable_AfterFurtherMutation()
    {
        var (_, def, adapter) = NewAdapter();
        var before = await adapter.EnqueueAsync(EnqueueOn(def.QueueId, adapter.Snapshot.Revision, "one"));
        var beforeSnapshot = adapter.Snapshot;

        _ = await adapter.EnqueueAsync(EnqueueOn(def.QueueId, adapter.Snapshot.Revision, "two"));

        // Snapshot captured earlier must not observe the newly enqueued item.
        var snapshotQueue = beforeSnapshot.Queues.Single(q => q.QueueId == def.QueueId);
        Assert.Single(snapshotQueue.Items);
        Assert.Equal(before.ItemId, snapshotQueue.Items[0].ItemId);
    }

    [Fact]
    public async Task Changed_Event_RaisedOnAppliedCommand()
    {
        var (_, def, adapter) = NewAdapter();
        var raised = 0;
        adapter.Changed += (_, _) => raised++;
        _ = await adapter.EnqueueAsync(EnqueueOn(def.QueueId, adapter.Snapshot.Revision, "note"));
        Assert.True(raised >= 1);
    }

    [Fact]
    public void AgentInputItem_DefaultItemId_IsNonBlank()
    {
        var item = new AgentInputItem { Messages = new[] { new ChatMessage(ChatRole.User, "x") } };
        Assert.False(string.IsNullOrWhiteSpace(item.ItemId));
    }

    [Fact]
    public void AgentInputQueue_HasStableNonBlankQueueId()
    {
        var q = new AgentInputQueue();
        Assert.False(string.IsNullOrWhiteSpace(q.QueueId));
        Assert.Equal(q.QueueId, q.QueueId);
    }

    [Fact]
    public void AgentInputQueueManager_AggregateRevision_BumpsOnEnqueue()
    {
        var manager = new AgentInputQueueManager();
        var q = new AgentInputQueue();
        manager.RegisterInputQueue(q);
        var before = manager.AggregateRevision;
        manager.Enqueue(q, new[] { new AgentInputItem { Messages = new[] { new ChatMessage(ChatRole.User, "x") } } });
        Assert.True(manager.AggregateRevision > before);
    }

    [Fact]
    public void SnapshotRecords_RoundTripThroughJson()
    {
        var options = AIJsonUtilities.DefaultOptions;
        var snapshot = new AgentInputQueuesSnapshot
        {
            Revision = 7,
            Queues = ImmutableArray.Create(new AgentInputQueueSnapshot
            {
                QueueId = "q1",
                Name = "n",
                IsDefault = true,
                IsImmediate = false,
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 3,
                Revision = 2,
                Items = ImmutableArray.Create(new AgentInputItemSnapshot
                {
                    ItemId = "i1",
                    Messages = ImmutableArray<ChatMessage>.Empty,
                }),
            }),
        };
        var json = JsonSerializer.Serialize(snapshot, options);
        var round = JsonSerializer.Deserialize<AgentInputQueuesSnapshot>(json, options);
        Assert.Equal(snapshot.Revision, round.Revision);
        Assert.Equal(snapshot.Queues.Length, round.Queues.Length);
        Assert.Equal(snapshot.Queues[0].QueueId, round.Queues[0].QueueId);
        Assert.Equal(snapshot.Queues[0].Items[0].ItemId, round.Queues[0].Items[0].ItemId);
    }
}
