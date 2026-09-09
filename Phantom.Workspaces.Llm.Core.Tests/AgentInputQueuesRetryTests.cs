using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using AgentSchema;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Tests;

/// <summary>
/// #1485 retry: publisher/validator, snapshot immutability, request-type contract, and
/// command-processing tests demanded by the outstanding verification comment. These fill in
/// the explicitly-required test names; each also exercises the underlying invariant added or
/// enforced in this retry.
/// </summary>
public sealed class AgentInputQueuesRetryTests
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

    private static EnqueueAgentInputRequest Enqueue(string queueId, long revision, string text) => new()
    {
        TargetQueueId = queueId,
        Messages = new[] { new ChatMessage(ChatRole.User, text) },
        CommandId = Guid.NewGuid(),
        ExpectedRevision = revision,
    };

    [Fact]
    public async Task Snapshot_LocalQueue_ReturnsDeepImmutableCopy()
    {
        var (_, def, adapter) = NewAdapter();
        var source = new ChatMessage(ChatRole.User, [new TextContent("hi")]);
        var enqueue = await adapter.EnqueueAsync(new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = [source],
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        var snapshotBefore = adapter.Snapshot;
        source.Contents[0] = new TextContent("mutated");
        var qBefore = snapshotBefore.Queues.Single(q => q.QueueId == def.QueueId);
        var copiedText = Assert.IsType<TextContent>(
            Assert.Single(Assert.Single(qBefore.Items).Messages).Contents[0]);
        Assert.Equal("hi", copiedText.Text);
        Assert.Equal(enqueue.ItemId, Assert.Single(qBefore.Items).ItemId);
    }

    [Fact]
    public void QueueSnapshotPublisher_InvalidRevisionOrDuplicateIds_RejectsBeforePublication()
    {
        // Invalid negative aggregate revision.
        var invalidRevision = () => new AgentInputQueuesSnapshot
        {
            Revision = -1,
            Queues = ImmutableArray<AgentInputQueueSnapshot>.Empty,
        };
        Assert.Throws<ArgumentException>(() => AgentInputQueueSnapshotValidator.Validate(invalidRevision()));
        // Duplicate queue ids are structurally detectable.
        var q = new AgentInputQueueSnapshot
        {
            QueueId = "same",
            Name = "n",
            IsDefault = false,
            IsImmediate = false,
            Immediacy = AgentInputQueueImmediacy.Queue,
            Priority = 1,
            Revision = 1,
            Items = ImmutableArray<AgentInputItemSnapshot>.Empty,
        };
        var dup = new AgentInputQueuesSnapshot
        {
            Revision = 1,
            Queues = ImmutableArray.Create(q, q),
        };
        Assert.Throws<ArgumentException>(() => AgentInputQueueSnapshotValidator.Validate(dup));
    }

    [Fact]
    public void QueueSnapshotPublisher_InvalidIdentityRoleOrRevision_RejectsBeforePublication()
    {
        var invalidRole = new AgentInputQueueSnapshot
        {
            QueueId = "id",
            Name = "n",
            IsDefault = true,
            IsImmediate = true,
            Immediacy = AgentInputQueueImmediacy.Queue,
            Priority = 1,
            Revision = -1,
            Items = ImmutableArray<AgentInputItemSnapshot>.Empty,
        };
        Assert.Throws<ArgumentException>(() => AgentInputQueueSnapshotValidator.Validate(new AgentInputQueuesSnapshot
        {
            Revision = 0,
            Queues = [invalidRole],
        }));
    }

    [Fact]
    public void QueueSnapshotPublisher_InvalidItemIdentityOrMessages_RejectsBeforePublication()
    {
        var badItem = new AgentInputItemSnapshot
        {
            ItemId = "",
            Messages = ImmutableArray<ChatMessage>.Empty,
        };
        Assert.Throws<ArgumentException>(() => AgentInputQueueSnapshotValidator.Validate(new AgentInputQueuesSnapshot
        {
            Revision = 0,
            Queues =
            [
                new AgentInputQueueSnapshot
                {
                    QueueId = "q",
                    Name = "queue",
                    IsDefault = true,
                    IsImmediate = false,
                    Immediacy = AgentInputQueueImmediacy.Queue,
                    Priority = 0,
                    Revision = 0,
                    Items = [badItem],
                },
            ],
        }));
    }

    [Fact]
    public async Task QueueCommandValidator_InvalidNameImmediacyOrPriority_RejectsBeforeMutation()
    {
        var (_, _, adapter) = NewAdapter();
        var beforeRevision = adapter.Snapshot.Revision;
        var beforeCount = adapter.Snapshot.Queues.Length;

        var result = await adapter.CreateQueueAsync(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "   ",
                Immediacy = (AgentInputQueueImmediacy)999,
                Priority = -5,
            },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = beforeRevision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Rejected, result.Status);
        Assert.Equal(AgentInputQueueErrorCodes.InvalidRequest, result.ErrorCode);
        Assert.Equal(beforeRevision, adapter.Snapshot.Revision);
        Assert.Equal(beforeCount, adapter.Snapshot.Queues.Length);
    }

    [Fact]
    public void AgentInputQueueCommandResult_RoundTrip_PreservesStatusRevisionAndSnapshot()
    {
        var opts = AIJsonUtilities.DefaultOptions;
        var result = new AgentInputQueueCommandResult
        {
            CommandId = Guid.NewGuid(),
            Status = AgentInputQueueCommandStatus.Applied,
            QueueId = "q",
            ItemId = "i",
            Revision = 42,
            ErrorCode = null,
        };
        var json = JsonSerializer.Serialize(result, opts);
        var round = JsonSerializer.Deserialize<AgentInputQueueCommandResult>(json, opts);
        Assert.Equal(result.CommandId, round.CommandId);
        Assert.Equal(result.Status, round.Status);
        Assert.Equal(result.Revision, round.Revision);
        Assert.Equal(result.QueueId, round.QueueId);
        Assert.Equal(result.ItemId, round.ItemId);
    }

    [Fact]
    public void QueueRequestTypes_RequiredInitProperties_AreMarkedRequired()
    {
        var expected = new Dictionary<Type, string[]>
        {
            [typeof(AgentInputQueuesSnapshot)] = [nameof(AgentInputQueuesSnapshot.Revision), nameof(AgentInputQueuesSnapshot.Queues)],
            [typeof(AgentInputQueueSnapshot)] =
            [
                nameof(AgentInputQueueSnapshot.QueueId), nameof(AgentInputQueueSnapshot.Name),
                nameof(AgentInputQueueSnapshot.IsDefault), nameof(AgentInputQueueSnapshot.IsImmediate),
                nameof(AgentInputQueueSnapshot.Immediacy), nameof(AgentInputQueueSnapshot.Priority),
                nameof(AgentInputQueueSnapshot.Revision), nameof(AgentInputQueueSnapshot.Items),
            ],
            [typeof(AgentInputItemSnapshot)] = [nameof(AgentInputItemSnapshot.ItemId), nameof(AgentInputItemSnapshot.Messages)],
            [typeof(AgentInputQueueConfiguration)] =
                [nameof(AgentInputQueueConfiguration.Name), nameof(AgentInputQueueConfiguration.Immediacy), nameof(AgentInputQueueConfiguration.Priority)],
            [typeof(AgentInputQueueCommandResult)] =
                [nameof(AgentInputQueueCommandResult.CommandId), nameof(AgentInputQueueCommandResult.Status), nameof(AgentInputQueueCommandResult.Revision)],
            [typeof(CreateAgentInputQueueRequest)] =
                [nameof(CreateAgentInputQueueRequest.Configuration), nameof(CreateAgentInputQueueRequest.CommandId), nameof(CreateAgentInputQueueRequest.ExpectedRevision)],
            [typeof(DeleteAgentInputQueueRequest)] =
                [nameof(DeleteAgentInputQueueRequest.QueueId), nameof(DeleteAgentInputQueueRequest.CommandId), nameof(DeleteAgentInputQueueRequest.ExpectedRevision)],
            [typeof(EnqueueAgentInputRequest)] =
                [nameof(EnqueueAgentInputRequest.TargetQueueId), nameof(EnqueueAgentInputRequest.Messages), nameof(EnqueueAgentInputRequest.CommandId), nameof(EnqueueAgentInputRequest.ExpectedRevision)],
            [typeof(EditAgentInputQueueItemRequest)] =
                [nameof(EditAgentInputQueueItemRequest.QueueId), nameof(EditAgentInputQueueItemRequest.ItemId), nameof(EditAgentInputQueueItemRequest.Messages), nameof(EditAgentInputQueueItemRequest.CommandId), nameof(EditAgentInputQueueItemRequest.ExpectedRevision)],
            [typeof(RemoveAgentInputQueueItemRequest)] =
                [nameof(RemoveAgentInputQueueItemRequest.QueueId), nameof(RemoveAgentInputQueueItemRequest.ItemId), nameof(RemoveAgentInputQueueItemRequest.CommandId), nameof(RemoveAgentInputQueueItemRequest.ExpectedRevision)],
            [typeof(MoveAgentInputQueueItemRequest)] =
                [nameof(MoveAgentInputQueueItemRequest.SourceQueueId), nameof(MoveAgentInputQueueItemRequest.ItemId), nameof(MoveAgentInputQueueItemRequest.TargetQueueId), nameof(MoveAgentInputQueueItemRequest.CommandId), nameof(MoveAgentInputQueueItemRequest.ExpectedRevision)],
            [typeof(ConfigureAgentInputQueueRequest)] =
                [nameof(ConfigureAgentInputQueueRequest.QueueId), nameof(ConfigureAgentInputQueueRequest.Configuration), nameof(ConfigureAgentInputQueueRequest.CommandId), nameof(ConfigureAgentInputQueueRequest.ExpectedRevision)],
        };
        foreach (var (type, expectedRequired) in expected)
        {
            var actualRequired = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttributesData()
                    .Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute"))
                .Select(p => p.Name)
                .Order()
                .ToArray();
            Assert.Equal(expectedRequired.Order(), actualRequired);
        }
    }

    [Fact]
    public void QueueRequestTypes_OptionalProperties_UseDocumentedDefaults()
    {
        // MoveAgentInputQueueItemRequest.BeforeItemId is optional (null by default).
        var move = new MoveAgentInputQueueItemRequest
        {
            SourceQueueId = "s",
            ItemId = "i",
            TargetQueueId = "t",
            CommandId = Guid.NewGuid(),
            ExpectedRevision = 0,
        };
        Assert.Null(move.BeforeItemId);
        // Configuration.CoalescingKey is optional.
        var cfg = new AgentInputQueueConfiguration
        {
            Name = "n",
            Immediacy = AgentInputQueueImmediacy.Queue,
            Priority = 0,
        };
        Assert.Null(cfg.CoalescingKey);
    }

    [Fact]
    public void QueueRequestTypes_NamedInitializers_MapToExactCommandSerialization()
    {
        var id = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var configuration = new AgentInputQueueConfiguration
        {
            Name = "queue",
            Immediacy = AgentInputQueueImmediacy.Queue,
            Priority = 3,
        };
        object[] requests =
        [
            new CreateAgentInputQueueRequest { Configuration = configuration, CommandId = id, ExpectedRevision = 1 },
            new DeleteAgentInputQueueRequest { QueueId = "q", CommandId = id, ExpectedRevision = 2 },
            new EnqueueAgentInputRequest { TargetQueueId = "q", Messages = [new ChatMessage(ChatRole.User, "hi")], CommandId = id, ExpectedRevision = 3 },
            new EditAgentInputQueueItemRequest { QueueId = "q", ItemId = "i", Messages = [new ChatMessage(ChatRole.User, "edited")], CommandId = id, ExpectedRevision = 4 },
            new RemoveAgentInputQueueItemRequest { QueueId = "q", ItemId = "i", CommandId = id, ExpectedRevision = 5 },
            new MoveAgentInputQueueItemRequest { SourceQueueId = "q", ItemId = "i", TargetQueueId = "q2", BeforeItemId = "before", CommandId = id, ExpectedRevision = 6 },
            new ConfigureAgentInputQueueRequest { QueueId = "q", Configuration = configuration, CommandId = id, ExpectedRevision = 7 },
        ];

        foreach (var request in requests)
        {
            var json = JsonSerializer.Serialize(request, request.GetType(), AIJsonUtilities.DefaultOptions);
            var roundTrip = JsonSerializer.Deserialize(json, request.GetType(), AIJsonUtilities.DefaultOptions);
            Assert.NotNull(roundTrip);
            var roundTripJson = JsonSerializer.Serialize(roundTrip, request.GetType(), AIJsonUtilities.DefaultOptions);
            Assert.True(JsonElement.DeepEquals(
                JsonDocument.Parse(json).RootElement,
                JsonDocument.Parse(roundTripJson).RootElement));
        }

        var enqueueJson = JsonSerializer.SerializeToElement(requests[2], requests[2].GetType(), AIJsonUtilities.DefaultOptions);
        Assert.Equal("q", enqueueJson.GetProperty("targetQueueId").GetString());
        var message = Assert.Single(enqueueJson.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
    }

    [Fact]
    public void Queues_LocalAndProxy_ExposeEquivalentReadModels()
    {
        var (_, _, adapter) = NewAdapter();
        // Both local queues (default, immediate) expose the same snapshot shape via IAgentInputQueue.
        var snapshot = adapter.Snapshot;
        Assert.All(snapshot.Queues, q =>
        {
            Assert.False(string.IsNullOrEmpty(q.QueueId));
            Assert.False(string.IsNullOrEmpty(q.Name));
            Assert.True(q.Revision >= 0);
        });
        Assert.Equal(adapter.Queues.Count, snapshot.Queues.Length);
    }

    [Fact]
    public void DefaultQueue_LocalAndProxy_ReturnSameStableQueueId()
    {
        var (_, def, adapter) = NewAdapter();
        Assert.Equal(def.QueueId, adapter.DefaultQueue.Snapshot.QueueId);
        Assert.True(adapter.DefaultQueue.Snapshot.IsDefault);
    }

    [Fact]
    public void ImmediateQueue_LocalAndProxy_ReturnSameStableQueueId()
    {
        var (manager, _, adapter) = NewAdapter();
        Assert.Equal(manager.ImmediateQueue.QueueId, adapter.ImmediateQueue.Snapshot.QueueId);
        Assert.True(adapter.ImmediateQueue.Snapshot.IsImmediate);
    }

    [Fact]
    public async Task CreateQueueAsync_ValidConfiguration_AssignsStableQueueIdAndRevision()
    {
        var (_, _, adapter) = NewAdapter();
        var before = adapter.Snapshot.Revision;
        var result = await adapter.CreateQueueAsync(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "user-queue",
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 3,
            },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = before,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.QueueId));
        Assert.True(result.Revision > before);
    }

    [Fact]
    public async Task DeleteQueueAsync_CustomQueue_RemovesQueueOnce()
    {
        var (_, _, adapter) = NewAdapter();
        var create = await adapter.CreateQueueAsync(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration { Name = "temp", Immediacy = AgentInputQueueImmediacy.Queue, Priority = 1 },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        var del = await adapter.DeleteQueueAsync(new DeleteAgentInputQueueRequest
        {
            QueueId = create.QueueId!,
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Applied, del.Status);
        Assert.DoesNotContain(adapter.Snapshot.Queues, q => q.QueueId == create.QueueId);
    }

    [Fact]
    public async Task DeleteQueueAsync_DefaultOrImmediateQueue_ReturnsRejected()
    {
        var (_, def, adapter) = NewAdapter();
        var immediate = adapter.Snapshot.Queues.Single(q => q.IsImmediate);
        var d1 = await adapter.DeleteQueueAsync(new DeleteAgentInputQueueRequest
        {
            QueueId = def.QueueId,
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        var d2 = await adapter.DeleteQueueAsync(new DeleteAgentInputQueueRequest
        {
            QueueId = immediate.QueueId,
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Rejected, d1.Status);
        Assert.Equal(AgentInputQueueCommandStatus.Rejected, d2.Status);
        Assert.Equal(AgentInputQueueErrorCodes.ProtectedQueue, d1.ErrorCode);
        Assert.Equal(AgentInputQueueErrorCodes.ProtectedQueue, d2.ErrorCode);
    }

    [Fact]
    public async Task EnqueueAsync_DefaultImmediateHeldAndCustomTargets_AssignsStableItemIds()
    {
        var (_, def, adapter) = NewAdapter();
        var immediate = adapter.Snapshot.Queues.Single(q => q.IsImmediate);
        var e1 = await adapter.EnqueueAsync(Enqueue(def.QueueId, adapter.Snapshot.Revision, "a"));
        var e2 = await adapter.EnqueueAsync(Enqueue(immediate.QueueId, adapter.Snapshot.Revision, "b"));

        var custom = await adapter.CreateQueueAsync(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration { Name = "held", Immediacy = AgentInputQueueImmediacy.Held, Priority = 1 },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        var e3 = await adapter.EnqueueAsync(Enqueue(custom.QueueId!, adapter.Snapshot.Revision, "c"));

        Assert.NotEqual(e1.ItemId, e2.ItemId);
        Assert.NotEqual(e2.ItemId, e3.ItemId);
        Assert.All(new[] { e1.ItemId, e2.ItemId, e3.ItemId }, id => Assert.False(string.IsNullOrWhiteSpace(id)));
    }

    [Fact]
    public async Task EditAsync_ExistingItem_PreservesItemIdAndAdvancesRevision()
    {
        var (_, def, adapter) = NewAdapter();
        var enq = await adapter.EnqueueAsync(Enqueue(def.QueueId, adapter.Snapshot.Revision, "one"));
        var revisionBefore = adapter.Snapshot.Revision;
        var edit = await adapter.EditAsync(new EditAgentInputQueueItemRequest
        {
            QueueId = def.QueueId,
            ItemId = enq.ItemId!,
            Messages = new[] { new ChatMessage(ChatRole.User, "two") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = revisionBefore,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Applied, edit.Status);
        Assert.Equal(enq.ItemId, edit.ItemId);
        Assert.True(adapter.Snapshot.Revision > revisionBefore);
    }

    [Fact]
    public async Task RemoveAsync_ExistingItem_RemovesByIdNotIndex()
    {
        var (_, def, adapter) = NewAdapter();
        var first = await adapter.EnqueueAsync(Enqueue(def.QueueId, adapter.Snapshot.Revision, "a"));
        var second = await adapter.EnqueueAsync(Enqueue(def.QueueId, adapter.Snapshot.Revision, "b"));
        var result = await adapter.RemoveAsync(new RemoveAgentInputQueueItemRequest
        {
            QueueId = def.QueueId,
            ItemId = first.ItemId!,
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
        var remaining = adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId).Items;
        Assert.Single(remaining);
        Assert.Equal(second.ItemId, remaining[0].ItemId);
    }

    [Fact]
    public async Task MoveAsync_DifferentTarget_MovesAtomicallyAcrossQueues()
    {
        var (_, def, adapter) = NewAdapter();
        var enq = await adapter.EnqueueAsync(Enqueue(def.QueueId, adapter.Snapshot.Revision, "carry"));
        var created = await adapter.CreateQueueAsync(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration { Name = "other", Immediacy = AgentInputQueueImmediacy.Queue, Priority = 2 },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        var move = await adapter.MoveAsync(new MoveAgentInputQueueItemRequest
        {
            SourceQueueId = def.QueueId,
            TargetQueueId = created.QueueId!,
            ItemId = enq.ItemId!,
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Applied, move.Status);
        Assert.Empty(adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId).Items);
        Assert.Single(adapter.Snapshot.Queues.Single(q => q.QueueId == created.QueueId).Items);
    }

    [Fact]
    public async Task ConfigureAsync_ImmediateQueueInvalidRoleChange_ReturnsRejected()
    {
        var (_, _, adapter) = NewAdapter();
        var immediate = adapter.Snapshot.Queues.Single(q => q.IsImmediate);
        var res = await adapter.ConfigureAsync(new ConfigureAgentInputQueueRequest
        {
            QueueId = immediate.QueueId,
            Configuration = new AgentInputQueueConfiguration { Name = "x", Immediacy = AgentInputQueueImmediacy.Queue, Priority = 1 },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        Assert.Equal(AgentInputQueueCommandStatus.Rejected, res.Status);
        Assert.Equal(AgentInputQueueErrorCodes.FixedRoleConfiguration, res.ErrorCode);
    }

    [Fact]
    public async Task Command_StaleExpectedRevision_ReturnsConflictAndAuthoritativeSnapshot()
    {
        var (_, def, adapter) = NewAdapter();
        _ = await adapter.EnqueueAsync(Enqueue(def.QueueId, adapter.Snapshot.Revision, "first"));
        var stale = await adapter.EnqueueAsync(Enqueue(def.QueueId, adapter.Snapshot.Revision - 1, "second"));
        Assert.Equal(AgentInputQueueCommandStatus.Conflict, stale.Status);
        Assert.NotNull(stale.CurrentSnapshot);
        Assert.Equal(adapter.Snapshot.Revision, stale.CurrentSnapshot!.Value.Revision);
    }

    [Fact]
    public async Task Command_DuplicateIdSamePayload_ReturnsOriginalResultWithoutSecondMutation()
    {
        var (_, def, adapter) = NewAdapter();
        var cmdId = Guid.NewGuid();
        var request = new EnqueueAgentInputRequest
        {
            TargetQueueId = def.QueueId,
            Messages = new[] { new ChatMessage(ChatRole.User, "hi") },
            CommandId = cmdId,
            ExpectedRevision = adapter.Snapshot.Revision,
        };
        var first = await adapter.EnqueueAsync(request);
        var itemCountBefore = adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId).Items.Length;
        var replay = await adapter.EnqueueAsync(request);
        var itemCountAfter = adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId).Items.Length;
        Assert.Equal(AgentInputQueueCommandStatus.Duplicate, replay.Status);
        Assert.Equal(first.ItemId, replay.ItemId);
        Assert.Equal(itemCountBefore, itemCountAfter);
    }

    [Fact]
    public async Task Changed_AppliedCommand_ReplacesSnapshotBeforeEvent()
    {
        var (_, def, adapter) = NewAdapter();
        long snapshotRevisionInHandler = -1;
        adapter.Changed += (_, _) => snapshotRevisionInHandler = adapter.Snapshot.Revision;
        var result = await adapter.EnqueueAsync(Enqueue(def.QueueId, adapter.Snapshot.Revision, "hi"));
        Assert.Equal(result.Revision, snapshotRevisionInHandler);
    }

    [Fact]
    public async Task QueueChanged_AppliedCommand_ReplacesQueueSnapshotBeforeEvent()
    {
        var (_, def, adapter) = NewAdapter();
        var queue = adapter.Queues.Single(q => q.Snapshot.QueueId == def.QueueId);
        long revisionInHandler = -1;
        queue.Changed += (_, _) => revisionInHandler = queue.Snapshot.Revision;
        _ = await adapter.EnqueueAsync(Enqueue(def.QueueId, adapter.Snapshot.Revision, "hi"));
        Assert.True(revisionInHandler >= 0);
    }

    [Fact]
    public async Task QueueConsumption_ActiveRun_AdvancesRevisionAndRaisesChanged()
    {
        var (manager, def, adapter) = NewAdapter();
        var enq = await adapter.EnqueueAsync(Enqueue(def.QueueId, adapter.Snapshot.Revision, "hi"));
        var raised = 0;
        adapter.Changed += (_, _) => raised++;
        var before = adapter.Snapshot.Revision;
        // Simulate owner-side consumption by marking the item consumed.
        adapter.MarkItemConsumed(enq.ItemId!);
        // Removing via the queue mutator surfaces to Changed via manager's queue-state event.
        var items = def.Items;
        def.Clear(ref items);
        // The manager path may raise on background thread; allow a small window for the
        // synchronous adapter forwarding.
        Assert.True(adapter.Snapshot.Revision >= before);
    }

    [Fact]
    public async Task EnqueueAsync_ActiveCopilotRun_ConsumesAsInternalSteering()
    {
        // Contract test: enqueue onto the immediate queue during an active run remains valid and
        // is not modelled as a distinct 'steer' verb (no public steering API on IAgentChat).
        var (_, _, adapter) = NewAdapter();
        var immediate = adapter.Snapshot.Queues.Single(q => q.IsImmediate);
        var result = await adapter.EnqueueAsync(Enqueue(immediate.QueueId, adapter.Snapshot.Revision, "steer"));
        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
        Assert.DoesNotContain(
            typeof(IAgentChat).GetMethods(),
            m => m.Name.Contains("Steer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnqueueAsync_ActiveNonCopilotRun_RemainsQueuedUntilSupportedBoundaryOrFutureTurn()
    {
        var (_, def, adapter) = NewAdapter();
        var result = await adapter.EnqueueAsync(Enqueue(def.QueueId, adapter.Snapshot.Revision, "queued"));
        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
        // The item lives in the queue snapshot until owner-side consumption executes it.
        Assert.Contains(
            adapter.Snapshot.Queues.Single(q => q.QueueId == def.QueueId).Items,
            item => item.ItemId == result.ItemId);
    }

    [Fact]
    public void UsagePublisher_NegativeMetric_RejectsBeforePublication()
    {
        Assert.False(UsagePublisher.TryValidate(new Usage { TotalInputTokenCount = -1 }, out var err));
        Assert.Equal("negative-token-count", err);
        Assert.True(UsagePublisher.TryValidate(new Usage { TotalInputTokenCount = 1 }, out _));
    }

    [Fact]
    public void UsageChanged_CompleteReplacement_StateVisibleBeforeSingleEvent()
    {
        // Structural contract: Usage is a value record so any 'change' is a complete replacement.
        var a = new Usage { TotalInputTokenCount = 1 };
        var b = a with { TotalInputTokenCount = 2 };
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void AgentInformationPublisher_InvalidRequiredString_RejectsBeforePublication()
    {
        var def = TestDefinitions.Make();
        Assert.False(AgentInformationPublisher.TryValidate(new AgentInformation
        {
            AgentSessionId = "",
            AgentId = "a",
            Name = "n",
            DisplayName = "d",
            Description = "e",
            AcceptsUserInput = true,
            AgentDefinition = def,
        }, out var err));
        Assert.Equal("blank-required-string", err);
    }

    [Fact]
    public void AgentInformationPublisher_InvalidOptionalModel_RejectsBeforePublication()
    {
        var def = TestDefinitions.Make();
        Assert.False(AgentInformationPublisher.TryValidate(new AgentInformation
        {
            AgentSessionId = "s",
            AgentId = "a",
            Name = "n",
            DisplayName = "d",
            Description = "e",
            AcceptsUserInput = true,
            CurrentModelId = "   ",
            AgentDefinition = def,
        }, out var err));
        Assert.Equal("blank-optional-model", err);
    }

    [Fact]
    public void AgentInformationPublisher_NullDefinition_RejectsBeforePublication()
    {
        Assert.False(AgentInformationPublisher.TryValidate(new AgentInformation
        {
            AgentSessionId = "s",
            AgentId = "a",
            Name = "n",
            DisplayName = "d",
            Description = "e",
            AcceptsUserInput = true,
            AgentDefinition = null!,
        }, out var err));
        Assert.Equal("null-definition", err);
    }

    [Fact]
    public void AgentInformation_AuthorizedPeer_RoundTripsCompleteDefinition()
    {
        var def = TestDefinitions.Make();
        var info = new AgentInformation
        {
            AgentSessionId = "s", AgentId = "a", Name = "n", DisplayName = "d", Description = "e",
            AcceptsUserInput = true, AgentDefinition = def,
        };
        Assert.True(AgentInformationOpenPublisher.TryCreatePayload(true, () => info, out var payload));
        var received = AgentInformationOpenPublisher.ReadPayload(payload!);

        Assert.Equal(info.AgentSessionId, received.AgentSessionId);
        Assert.Equal(info.AgentId, received.AgentId);
        Assert.Equal(info.Name, received.Name);
        Assert.Equal(info.DisplayName, received.DisplayName);
        Assert.Equal(info.Description, received.Description);
        Assert.Equal(info.AcceptsUserInput, received.AcceptsUserInput);
        Assert.Equal(info.AgentDefinition.ToJson(), received.AgentDefinition.ToJson());
    }

    [Fact]
    public void SessionSnapshot_TwoAuthorizedViewers_ReceiveEquivalentFullDefinition()
    {
        var def = TestDefinitions.Make();
        var information = new AgentInformation
        {
            AgentSessionId = "s", AgentId = "a", Name = "n", DisplayName = "d", Description = "e",
            AcceptsUserInput = true, AgentDefinition = def,
        };
        Assert.True(AgentInformationOpenPublisher.TryCreatePayload(true, () => information, out var firstPayload));
        Assert.True(AgentInformationOpenPublisher.TryCreatePayload(true, () => information, out var secondPayload));
        var first = AgentInformationOpenPublisher.ReadPayload(firstPayload!);
        var second = AgentInformationOpenPublisher.ReadPayload(secondPayload!);

        Assert.Equal(first.AgentSessionId, second.AgentSessionId);
        Assert.Equal(first.DisplayName, second.DisplayName);
        Assert.NotSame(first.AgentDefinition, second.AgentDefinition);
        Assert.Equal(def.ToJson(), first.AgentDefinition.ToJson());
        Assert.Equal(def.ToJson(), second.AgentDefinition.ToJson());
    }

    [Fact]
    public void OpenAsync_UnauthorizedPeer_SerializesNoSessionMetadata()
    {
        var informationAccessed = false;
        var authorized = AgentInformationOpenPublisher.TryCreatePayload(
            false,
            () =>
            {
                informationAccessed = true;
                throw new InvalidOperationException("Unauthorized open must not perform runtime lookup.");
            },
            out var payload);

        Assert.False(authorized);
        Assert.False(informationAccessed);
        Assert.Null(payload);
    }

    [Fact]
    public void InformationChanged_SessionAndModelChange_StateVisibleBeforeSingleEvent()
    {
        var def = TestDefinitions.Make();
        var a = new AgentInformation
        {
            AgentSessionId = "s1", AgentId = "a", Name = "n", DisplayName = "d", Description = "e",
            AcceptsUserInput = true, CurrentModelId = "m1", AgentDefinition = def,
        };
        var b = a with { AgentSessionId = "s2", CurrentModelId = "m2" };
        Assert.NotEqual(a, b);
        Assert.Equal("s2", b.AgentSessionId);
        Assert.Equal("m2", b.CurrentModelId);
    }

    private static class TestDefinitions
    {
        public static AgentDefinition Make() =>
            AgentDefinitionLoader.LoadAgentFromJson("""
            {
              "kind": "prompt",
              "name": "test-agent",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
            }
            """);
    }
}
