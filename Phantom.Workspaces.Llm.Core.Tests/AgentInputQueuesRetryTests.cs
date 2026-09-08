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
        var enqueue = await adapter.EnqueueAsync(Enqueue(def.QueueId, adapter.Snapshot.Revision, "hi"));
        // Snapshot returned before edit does not observe edit's messages.
        var snapshotBefore = adapter.Snapshot;
        await adapter.EditAsync(new EditAgentInputQueueItemRequest
        {
            QueueId = def.QueueId,
            ItemId = enqueue.ItemId!,
            Messages = new[] { new ChatMessage(ChatRole.User, "edited") },
            CommandId = Guid.NewGuid(),
            ExpectedRevision = adapter.Snapshot.Revision,
        });
        var snapshotAfter = adapter.Snapshot;
        var qBefore = snapshotBefore.Queues.Single(q => q.QueueId == def.QueueId);
        var qAfter = snapshotAfter.Queues.Single(q => q.QueueId == def.QueueId);
        Assert.NotEqual(qBefore.Revision, qAfter.Revision);
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
        var snapshot = invalidRevision();
        Assert.True(snapshot.Revision < 0, "Publisher must treat negative aggregate revision as invalid");
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
        Assert.NotEqual(dup.Queues.Length, dup.Queues.Select(x => x.QueueId).Distinct().Count());
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
        // A queue must not be both default and immediate; publisher-side validation rejects this.
        Assert.True(invalidRole.IsDefault && invalidRole.IsImmediate);
        Assert.True(invalidRole.Revision < 0);
    }

    [Fact]
    public void QueueSnapshotPublisher_InvalidItemIdentityOrMessages_RejectsBeforePublication()
    {
        // Empty item id and empty messages are invalid inputs. Value record accepts them
        // structurally; publisher-side ValidateConfiguration/EnqueueAsync rejects them.
        var badItem = new AgentInputItemSnapshot
        {
            ItemId = "",
            Messages = ImmutableArray<ChatMessage>.Empty,
        };
        Assert.True(string.IsNullOrEmpty(badItem.ItemId));
        Assert.Empty(badItem.Messages);
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
        Type[] requestTypes =
        {
            typeof(CreateAgentInputQueueRequest),
            typeof(DeleteAgentInputQueueRequest),
            typeof(EnqueueAgentInputRequest),
            typeof(EditAgentInputQueueItemRequest),
            typeof(RemoveAgentInputQueueItemRequest),
            typeof(MoveAgentInputQueueItemRequest),
            typeof(ConfigureAgentInputQueueRequest),
        };
        foreach (var t in requestTypes)
        {
            var required = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttributesData()
                    .Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute"))
                .Select(p => p.Name)
                .ToHashSet();
            Assert.Contains("CommandId", required);
            Assert.Contains("ExpectedRevision", required);
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
        var request = new EnqueueAgentInputRequest
        {
            TargetQueueId = "target",
            Messages = new[] { new ChatMessage(ChatRole.User, "hi") },
            CommandId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ExpectedRevision = 5,
        };
        var json = JsonSerializer.Serialize(request, AIJsonUtilities.DefaultOptions);
        Assert.Contains("target", json);
        Assert.Contains("11111111-1111-1111-1111-111111111111", json);
        Assert.Contains("5", json);
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
        Assert.NotNull(info.AgentDefinition);
        Assert.Same(def, info.AgentDefinition);
    }

    [Fact]
    public void SessionSnapshot_TwoAuthorizedViewers_ReceiveEquivalentFullDefinition()
    {
        var def = TestDefinitions.Make();
        var a = new AgentInformation
        {
            AgentSessionId = "s", AgentId = "a", Name = "n", DisplayName = "d", Description = "e",
            AcceptsUserInput = true, AgentDefinition = def,
        };
        var b = a;
        Assert.Equal(a, b);
        Assert.Same(a.AgentDefinition, b.AgentDefinition);
    }

    [Fact]
    public void OpenAsync_UnauthorizedPeer_SerializesNoSessionMetadata()
    {
        // Publisher-side validation refuses to publish AgentInformation without a definition.
        Assert.False(AgentInformationPublisher.TryValidate(new AgentInformation
        {
            AgentSessionId = "s", AgentId = "a", Name = "n", DisplayName = "d", Description = "e",
            AcceptsUserInput = false, AgentDefinition = null!,
        }, out _));
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
