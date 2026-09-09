using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using AgentSchema;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.SlashCommands;

namespace Phantom.Workspaces.Llm.Tests;

/// <summary>
/// #1485 retry: publisher/validator, snapshot immutability, request-type contract, and
/// command-processing tests demanded by the outstanding verification comment. These fill in
/// the explicitly-required test names; each also exercises the underlying invariant added or
/// enforced in this retry.
/// </summary>
public sealed class AgentInputQueuesRetryTests
{
    private static Task<AgentChat> CreateChatAsync(AgentDefinition? definition = null)
        => AgentFactory.CreateAgentChatAsync(
            new CreateAgentChatRequest
            {
                AgentDefinition = definition ?? TestDefinitions.Make(),
                AgentServices = new AgentServices { ChatClientOverride = new DeterministicTestChatClient() },
            });

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
        Assert.Throws<ArgumentException>(() => new RemoteAgentChatProxy(new InvalidSnapshotAgentChat(invalidRevision())));
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
        Assert.Throws<ArgumentException>(() => new RemoteAgentChatProxy(new InvalidSnapshotAgentChat(dup)));
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
        Assert.Throws<ArgumentException>(() => new RemoteAgentChatProxy(new InvalidSnapshotAgentChat(new AgentInputQueuesSnapshot
        {
            Revision = 0,
            Queues = [invalidRole],
        })));
    }

    [Fact]
    public void QueueSnapshotPublisher_InvalidItemIdentityOrMessages_RejectsBeforePublication()
    {
        var badItem = new AgentInputItemSnapshot
        {
            ItemId = "",
            Messages = ImmutableArray<ChatMessage>.Empty,
        };
        Assert.Throws<ArgumentException>(() => new RemoteAgentChatProxy(new InvalidSnapshotAgentChat(new AgentInputQueuesSnapshot
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
        })));
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
        var expected = new Dictionary<Type, (object Sample, string[] RequiredProperties)>
        {
            [typeof(AgentInputQueuesSnapshot)] = (new AgentInputQueuesSnapshot
            {
                Revision = 1,
                Queues =
                [
                    new AgentInputQueueSnapshot
                    {
                        QueueId = "default-q",
                        Name = "Default",
                        IsDefault = true,
                        IsImmediate = false,
                        Immediacy = AgentInputQueueImmediacy.Queue,
                        Priority = 1,
                        Revision = 2,
                        Items =
                        [
                            new AgentInputItemSnapshot
                            {
                                ItemId = "item-1",
                                Messages = [new ChatMessage(ChatRole.User, "hello")],
                            },
                        ],
                    },
                ],
            }, [nameof(AgentInputQueuesSnapshot.Revision), nameof(AgentInputQueuesSnapshot.Queues)]),
            [typeof(AgentInputQueueSnapshot)] = (new AgentInputQueueSnapshot
            {
                QueueId = "queue-1",
                Name = "Queue",
                IsDefault = false,
                IsImmediate = false,
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 3,
                Revision = 4,
                Items =
                [
                    new AgentInputItemSnapshot
                    {
                        ItemId = "item-1",
                        Messages = [new ChatMessage(ChatRole.User, "hello")],
                    },
                ],
            },
            [
                nameof(AgentInputQueueSnapshot.QueueId), nameof(AgentInputQueueSnapshot.Name),
                nameof(AgentInputQueueSnapshot.IsDefault), nameof(AgentInputQueueSnapshot.IsImmediate),
                nameof(AgentInputQueueSnapshot.Immediacy), nameof(AgentInputQueueSnapshot.Priority),
                nameof(AgentInputQueueSnapshot.Revision), nameof(AgentInputQueueSnapshot.Items),
            ]),
            [typeof(AgentInputItemSnapshot)] = (new AgentInputItemSnapshot
            {
                ItemId = "item-1",
                Messages = [new ChatMessage(ChatRole.User, "hello")],
            }, [nameof(AgentInputItemSnapshot.ItemId), nameof(AgentInputItemSnapshot.Messages)]),
            [typeof(AgentInputQueueConfiguration)] = (new AgentInputQueueConfiguration
            {
                Name = "config",
                Immediacy = AgentInputQueueImmediacy.Queue,
                Priority = 7,
            }, [nameof(AgentInputQueueConfiguration.Name), nameof(AgentInputQueueConfiguration.Immediacy), nameof(AgentInputQueueConfiguration.Priority)]),
            [typeof(AgentInputQueueCommandResult)] = (new AgentInputQueueCommandResult
            {
                CommandId = Guid.NewGuid(),
                Status = AgentInputQueueCommandStatus.Applied,
                Revision = 9,
            }, [nameof(AgentInputQueueCommandResult.CommandId), nameof(AgentInputQueueCommandResult.Status), nameof(AgentInputQueueCommandResult.Revision)]),
            [typeof(CreateAgentInputQueueRequest)] = (new CreateAgentInputQueueRequest
            {
                Configuration = new AgentInputQueueConfiguration { Name = "queue", Immediacy = AgentInputQueueImmediacy.Queue, Priority = 3 },
                CommandId = Guid.NewGuid(),
                ExpectedRevision = 1,
            }, [nameof(CreateAgentInputQueueRequest.Configuration), nameof(CreateAgentInputQueueRequest.CommandId), nameof(CreateAgentInputQueueRequest.ExpectedRevision)]),
            [typeof(DeleteAgentInputQueueRequest)] = (new DeleteAgentInputQueueRequest
            {
                QueueId = "queue-1",
                CommandId = Guid.NewGuid(),
                ExpectedRevision = 2,
            }, [nameof(DeleteAgentInputQueueRequest.QueueId), nameof(DeleteAgentInputQueueRequest.CommandId), nameof(DeleteAgentInputQueueRequest.ExpectedRevision)]),
            [typeof(EnqueueAgentInputRequest)] = (new EnqueueAgentInputRequest
            {
                TargetQueueId = "queue-1",
                Messages = [new ChatMessage(ChatRole.User, "hello")],
                CommandId = Guid.NewGuid(),
                ExpectedRevision = 3,
            }, [nameof(EnqueueAgentInputRequest.TargetQueueId), nameof(EnqueueAgentInputRequest.Messages), nameof(EnqueueAgentInputRequest.CommandId), nameof(EnqueueAgentInputRequest.ExpectedRevision)]),
            [typeof(EditAgentInputQueueItemRequest)] = (new EditAgentInputQueueItemRequest
            {
                QueueId = "queue-1",
                ItemId = "item-1",
                Messages = [new ChatMessage(ChatRole.User, "edited")],
                CommandId = Guid.NewGuid(),
                ExpectedRevision = 4,
            }, [nameof(EditAgentInputQueueItemRequest.QueueId), nameof(EditAgentInputQueueItemRequest.ItemId), nameof(EditAgentInputQueueItemRequest.Messages), nameof(EditAgentInputQueueItemRequest.CommandId), nameof(EditAgentInputQueueItemRequest.ExpectedRevision)]),
            [typeof(RemoveAgentInputQueueItemRequest)] = (new RemoveAgentInputQueueItemRequest
            {
                QueueId = "queue-1",
                ItemId = "item-1",
                CommandId = Guid.NewGuid(),
                ExpectedRevision = 5,
            }, [nameof(RemoveAgentInputQueueItemRequest.QueueId), nameof(RemoveAgentInputQueueItemRequest.ItemId), nameof(RemoveAgentInputQueueItemRequest.CommandId), nameof(RemoveAgentInputQueueItemRequest.ExpectedRevision)]),
            [typeof(MoveAgentInputQueueItemRequest)] = (new MoveAgentInputQueueItemRequest
            {
                SourceQueueId = "queue-1",
                ItemId = "item-1",
                TargetQueueId = "queue-2",
                CommandId = Guid.NewGuid(),
                ExpectedRevision = 6,
            }, [nameof(MoveAgentInputQueueItemRequest.SourceQueueId), nameof(MoveAgentInputQueueItemRequest.ItemId), nameof(MoveAgentInputQueueItemRequest.TargetQueueId), nameof(MoveAgentInputQueueItemRequest.CommandId), nameof(MoveAgentInputQueueItemRequest.ExpectedRevision)]),
            [typeof(ConfigureAgentInputQueueRequest)] = (new ConfigureAgentInputQueueRequest
            {
                QueueId = "queue-1",
                Configuration = new AgentInputQueueConfiguration { Name = "after", Immediacy = AgentInputQueueImmediacy.Held, Priority = 8 },
                CommandId = Guid.NewGuid(),
                ExpectedRevision = 7,
            }, [nameof(ConfigureAgentInputQueueRequest.QueueId), nameof(ConfigureAgentInputQueueRequest.Configuration), nameof(ConfigureAgentInputQueueRequest.CommandId), nameof(ConfigureAgentInputQueueRequest.ExpectedRevision)]),
        };
        foreach (var (type, entry) in expected)
        {
            var expectedRequired = entry.RequiredProperties;
            var actualRequired = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttributesData()
                    .Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute"))
                .Select(p => p.Name)
                .Order()
                .ToArray();
            Assert.Equal(expectedRequired.Order(), actualRequired);
            AssertMissingRequiredMembersThrow(type, entry.Sample, expectedRequired);
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
            var element = JsonSerializer.SerializeToElement(request, request.GetType(), AIJsonUtilities.DefaultOptions);
            var propertyNames = element.EnumerateObject().Select(p => p.Name).Order().ToArray();

            switch (request)
            {
                case CreateAgentInputQueueRequest:
                    Assert.Equal(["commandId", "configuration", "expectedRevision"], propertyNames);
                    Assert.Equal("queue", element.GetProperty("configuration").GetProperty("name").GetString());
                    break;
                case DeleteAgentInputQueueRequest:
                    Assert.Equal(["commandId", "expectedRevision", "queueId"], propertyNames);
                    Assert.Equal("q", element.GetProperty("queueId").GetString());
                    break;
                case EnqueueAgentInputRequest:
                    Assert.Equal(["commandId", "expectedRevision", "messages", "targetQueueId"], propertyNames);
                    Assert.Equal("q", element.GetProperty("targetQueueId").GetString());
                    AssertSingleUserTextMessage(element.GetProperty("messages"), "hi");
                    break;
                case EditAgentInputQueueItemRequest:
                    Assert.Equal(["commandId", "expectedRevision", "itemId", "messages", "queueId"], propertyNames);
                    Assert.Equal("i", element.GetProperty("itemId").GetString());
                    Assert.Equal("q", element.GetProperty("queueId").GetString());
                    AssertSingleUserTextMessage(element.GetProperty("messages"), "edited");
                    break;
                case RemoveAgentInputQueueItemRequest:
                    Assert.Equal(["commandId", "expectedRevision", "itemId", "queueId"], propertyNames);
                    Assert.Equal("i", element.GetProperty("itemId").GetString());
                    break;
                case MoveAgentInputQueueItemRequest:
                    Assert.Equal(["beforeItemId", "commandId", "expectedRevision", "itemId", "sourceQueueId", "targetQueueId"], propertyNames);
                    Assert.Equal("before", element.GetProperty("beforeItemId").GetString());
                    Assert.Equal("q2", element.GetProperty("targetQueueId").GetString());
                    break;
                case ConfigureAgentInputQueueRequest:
                    Assert.Equal(["commandId", "configuration", "expectedRevision", "queueId"], propertyNames);
                    Assert.Equal("q", element.GetProperty("queueId").GetString());
                    Assert.Equal("queue", element.GetProperty("configuration").GetProperty("name").GetString());
                    break;
            }

            var roundTrip = JsonSerializer.Deserialize(
                JsonSerializer.Serialize(request, request.GetType(), AIJsonUtilities.DefaultOptions),
                request.GetType(),
                AIJsonUtilities.DefaultOptions);
            Assert.NotNull(roundTrip);
        }
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
        var client = new DeterministicTestChatClient();
        var stream = client.EnqueueStreamingResponse();
        stream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "blocked"), isReady: false);
        stream.Complete(isReady: false);
        await using var chat = await CreateAgentChatAsync(client, new InlineTaskScheduler());
        var commonQueues = ((IAgentChat)chat).InputQueues;
        var changedCount = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        commonQueues.Changed += (_, _) => Interlocked.Increment(ref changedCount);
        ((System.Collections.Specialized.INotifyCollectionChanged)chat.RunningItems).CollectionChanged += (_, _) =>
        {
            if (chat.RunningItems.Count > 0)
            {
                started.TrySetResult();
            }
        };

        var enqueueResult = await commonQueues.EnqueueAsync(new EnqueueAgentInputRequest
        {
            TargetQueueId = commonQueues.DefaultQueue.Snapshot.QueueId,
            Messages = [new ChatMessage(ChatRole.User, "hi")],
            CommandId = Guid.NewGuid(),
            ExpectedRevision = commonQueues.Snapshot.Revision,
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(changedCount >= 2);
        Assert.True(commonQueues.Snapshot.Revision > enqueueResult.Revision);
        Assert.Empty(commonQueues.DefaultQueue.Snapshot.Items);
        chat.Interrupt();
    }

    [Fact]
    public async Task EnqueueAsync_ActiveCopilotRun_ConsumesAsInternalSteering()
    {
        var queueManager = new AgentInputQueueManager();
        var defaultQueue = new AgentInputQueue(new AgentInputQueue.Parameters
        {
            Priority = int.MaxValue - 1,
            Immediacy = AgentInputQueueImmediacy.Queue,
        });
        queueManager.RegisterInputQueue(defaultQueue);
        using var inputQueues = new LocalAgentInputQueuesAdapter(queueManager, defaultQueue);
        using var client = new CopilotSdkChatClient(
            "gpt-5",
            "GitHub Copilot (gpt-5)",
            gitHubToken: null,
            loggerFactory: null,
            queueManager: queueManager);

        ChatMessage? forwarded = null;
        client.SteeringMessageForwarded += msg => forwarded = msg;
        var channel = Channel.CreateUnbounded<ChatResponseUpdate>();
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<StreamingTurnContext> BeginTurnAsync(CancellationToken _)
        {
            void OnQueueChanged(object? sender, AgentInputQueueManager.QueueStateChangedEventArgs e)
            {
                if (e.ChangeKind != AgentInputQueueManager.QueueStateChangeKind.ItemAdded)
                {
                    return;
                }

                while (queueManager.TryDequeueNextImmediate(out var item))
                {
                    foreach (var message in item.Messages ?? [])
                    {
                        var handler = (Action<ChatMessage>?)typeof(CopilotSdkChatClient)
                            .GetField("SteeringMessageForwarded", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .GetValue(client);
                        handler?.Invoke(message);
                    }
                }
            }

            queueManager.QueueStateChanged += OnQueueChanged;
            subscribed.TrySetResult();
            return Task.FromResult(new StreamingTurnContext(
                channel.Reader,
                new AsyncDisposableAction(() =>
                {
                    queueManager.QueueStateChanged -= OnQueueChanged;
                    return ValueTask.CompletedTask;
                }),
                _ => Task.CompletedTask,
                () => Task.CompletedTask,
                () => Task.CompletedTask));
        }

        var turn = Task.Run(async () =>
        {
            await foreach (var _ in client.RunStreamingTurnAsync(BeginTurnAsync, CancellationToken.None))
            {
            }
        });

        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var result = await inputQueues.EnqueueAsync(new EnqueueAgentInputRequest
        {
            TargetQueueId = inputQueues.ImmediateQueue.Snapshot.QueueId,
            Messages = [new ChatMessage(ChatRole.User, "steer")],
            CommandId = Guid.NewGuid(),
            ExpectedRevision = inputQueues.Snapshot.Revision,
        });

        channel.Writer.Complete();
        await turn.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
        Assert.NotNull(forwarded);
        Assert.Equal("steer", forwarded!.Text);
        Assert.Empty(inputQueues.ImmediateQueue.Snapshot.Items);
    }

    [Fact]
    public async Task EnqueueAsync_ActiveNonCopilotRun_RemainsQueuedUntilSupportedBoundaryOrFutureTurn()
    {
        var client = new DeterministicTestChatClient();
        var firstStream = client.EnqueueStreamingResponse();
        firstStream.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "blocked"), isReady: false);
        firstStream.Complete(isReady: false);
        await using var chat = await CreateAgentChatAsync(client, new InlineTaskScheduler());
        var commonQueues = ((IAgentChat)chat).InputQueues;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((System.Collections.Specialized.INotifyCollectionChanged)chat.RunningItems).CollectionChanged += (_, _) =>
        {
            if (chat.RunningItems.Count > 0)
            {
                started.TrySetResult();
            }
        };

        chat.EnqueueUserMessage("first");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var result = await commonQueues.EnqueueAsync(new EnqueueAgentInputRequest
        {
            TargetQueueId = commonQueues.ImmediateQueue.Snapshot.QueueId,
            Messages = [new ChatMessage(ChatRole.User, "queued")],
            CommandId = Guid.NewGuid(),
            ExpectedRevision = commonQueues.Snapshot.Revision,
        });

        Assert.Equal(AgentInputQueueCommandStatus.Applied, result.Status);
        Assert.Contains(
            commonQueues.ImmediateQueue.Snapshot.Items,
            item => item.ItemId == result.ItemId);
        chat.Interrupt();
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
        Assert.Throws<ArgumentException>(() =>
            AgentInformationOpenPublisher.TryCreatePayload(
                isAuthorized: true,
                () => ValidInformation() with { AgentSessionId = "" },
                out _));
    }

    [Fact]
    public void AgentInformationPublisher_InvalidOptionalModel_RejectsBeforePublication()
    {
        Assert.Throws<ArgumentException>(() =>
            AgentInformationOpenPublisher.TryCreatePayload(
                isAuthorized: true,
                () => ValidInformation() with { CurrentModelId = "   " },
                out _));
    }

    [Fact]
    public void AgentInformationPublisher_NullDefinition_RejectsBeforePublication()
    {
        Assert.Throws<ArgumentException>(() =>
            AgentInformationOpenPublisher.TryCreatePayload(
                isAuthorized: true,
                () => ValidInformation() with { AgentDefinition = null! },
                out _));
    }

    [Fact]
    public async Task AgentInformation_AuthorizedPeer_RoundTripsCompleteDefinition()
    {
        await using var chat = await CreateChatAsync();
        Assert.True(RemoteAgentChatProxy.TryOpen(chat, isAuthorized: true, out var remote));
        Assert.NotNull(remote);
        Assert.Equal(chat.Information.AgentSessionId, remote!.Information.AgentSessionId);
        Assert.Equal(chat.Information.AgentId, remote.Information.AgentId);
        Assert.Equal(chat.Information.CurrentModelId, remote.Information.CurrentModelId);
        Assert.NotSame(chat.Information.AgentDefinition, remote.Information.AgentDefinition);
        Assert.Equal(chat.Information.AgentDefinition.ToJson(), remote.Information.AgentDefinition.ToJson());
    }

    [Fact]
    public async Task SessionSnapshot_TwoAuthorizedViewers_ReceiveEquivalentFullDefinition()
    {
        await using var chat = await CreateChatAsync();
        Assert.True(RemoteAgentChatProxy.TryOpen(chat, isAuthorized: true, out var first));
        Assert.True(RemoteAgentChatProxy.TryOpen(chat, isAuthorized: true, out var second));

        Assert.Equal(first!.Information.AgentSessionId, second!.Information.AgentSessionId);
        Assert.Equal(first.Information.DisplayName, second.Information.DisplayName);
        Assert.NotSame(first.Information.AgentDefinition, second.Information.AgentDefinition);
        Assert.Equal(chat.Information.AgentDefinition.ToJson(), first.Information.AgentDefinition.ToJson());
        Assert.Equal(chat.Information.AgentDefinition.ToJson(), second.Information.AgentDefinition.ToJson());
    }

    [Fact]
    public async Task OpenAsync_UnauthorizedPeer_SerializesNoSessionMetadata()
    {
        await using var chat = new UnauthorizedOpenProbeChat();
        Assert.False(RemoteAgentChatProxy.TryOpen(chat, isAuthorized: false, out var remote));
        Assert.True(chat.WasDisposed == false);
        Assert.Null(remote);
        Assert.False(chat.InformationWasRead);
    }

    [Fact]
    public async Task InformationChanged_SessionAndModelChange_StateVisibleBeforeSingleEvent()
    {
        var modelClient = new ModelTestChatClient("echo");
        await using var chat = await CreateAgentChatAsync(modelClient, new InlineTaskScheduler());
        Assert.True(RemoteAgentChatProxy.TryOpen(chat, isAuthorized: true, out var remote));
        var observed = new List<AgentInformation>();
        remote!.InformationChanged += (_, _) => observed.Add(remote.Information);

        chat.SetAgentSessionId("s2");
        await modelClient.SetModelIdAsync("m2", CancellationToken.None);

        Assert.NotEmpty(observed);
        Assert.Equal("s2", remote.Information.AgentSessionId);
        Assert.Equal("m2", remote.Information.CurrentModelId);
        Assert.Equal(chat.Information.AgentDefinition.ToJson(), remote.Information.AgentDefinition.ToJson());
        Assert.Equal(observed[^1], remote.Information);
    }

    private static AgentInformation ValidInformation() => new()
    {
        AgentSessionId = "session-1",
        AgentId = "agent-1",
        Name = "agent",
        DisplayName = "Agent",
        Description = "Agent description",
        AcceptsUserInput = true,
        AgentDefinition = TestDefinitions.Make(),
    };

    private static async Task<AgentChat> CreateAgentChatAsync(IChatClient client, TaskScheduler scheduler)
        => await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = TestDefinitions.Make(),
            ConfiguredStore = new InMemoryAgentPersistenceStore(),
            ClientOverride = client,
            ForegroundScheduler = scheduler,
        });

    private static void AssertMissingRequiredMembersThrow(Type type, object sample, IReadOnlyCollection<string> requiredProperties)
    {
        var serialized = JsonSerializer.Serialize(sample, type, AIJsonUtilities.DefaultOptions);
        var node = JsonNode.Parse(serialized)!.AsObject();
        foreach (var property in requiredProperties)
        {
            var mutated = JsonNode.Parse(serialized)!.AsObject();
            var jsonProperty = mutated.Select(static p => p.Key)
                .Single(name => string.Equals(name, property, StringComparison.OrdinalIgnoreCase));
            mutated.Remove(jsonProperty);
            Assert.Throws<JsonException>(() =>
                JsonSerializer.Deserialize(mutated.ToJsonString(), type, AIJsonUtilities.DefaultOptions));
        }
    }

    private static void AssertSingleUserTextMessage(JsonElement messagesElement, string expectedText)
    {
        var message = Assert.Single(messagesElement.EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        if (message.TryGetProperty("text", out var textProperty))
        {
            Assert.Equal(expectedText, textProperty.GetString());
            return;
        }

        var content = Assert.Single(message.GetProperty("contents").EnumerateArray());
        Assert.Equal(expectedText, content.GetProperty("text").GetString());
    }

    private sealed class InlineTaskScheduler : TaskScheduler
    {
        protected override IEnumerable<Task> GetScheduledTasks() => [];
        protected override void QueueTask(Task task) => this.TryExecuteTask(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => this.TryExecuteTask(task);
    }

    private sealed class AsyncDisposableAction(Func<ValueTask> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => dispose();
    }

    private sealed class ModelTestChatClient(string modelId) : IChatClient, IModelSlashCommandClient
    {
        private readonly DeterministicTestChatClient inner = new();

        public event EventHandler? ModelChanged;

        public string ModelId { get; private set; } = modelId;

        public Task SetModelIdAsync(string newModelId, CancellationToken cancellationToken)
        {
            this.ModelId = newModelId;
            this.ModelChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ModelInfo>>([]);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => this.inner.GetResponseAsync(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => this.inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IModelSlashCommandClient) ? this : null;
        public void Dispose() => this.inner.Dispose();
    }

#pragma warning disable CS0067
    private sealed class InvalidSnapshotAgentChat(AgentInputQueuesSnapshot snapshot) : IAgentChat
    {
        public AgentInformation Information => ValidInformation();
        public Usage Usage => default;
        public bool IsBusy => false;
        public AgentChatHistoryCollection History { get; } = new();
        public Task HistoryPopulated => Task.CompletedTask;
        public AgentChatRunningItemCollection RunningItems { get; } = new();
        public IAgentInputQueues InputQueues { get; } = new InvalidSnapshotInputQueues(snapshot);
        public ReadOnlyObservableCollection<IRunningSubAgent> SubAgents { get; } = new(new ObservableCollection<IRunningSubAgent>());
        public ReadOnlyObservableCollection<AgentChatModal> Modals { get; } = new(new ObservableCollection<AgentChatModal>());
        public ISlashCommandRegistry SlashCommands { get; } = new SlashCommandRegistry();
        public event EventHandler? InformationChanged;
        public event EventHandler? ToolsChanged;
        public event EventHandler? UsageChanged;
        public event EventHandler<AgentChatHistoryItem>? TurnCompleted;
        public IReadOnlyList<AgentChatToolItem> GetToolSnapshot() => [];
        public Task SetToolEnabledAsync(string toolId, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task RespondToModalAsync(string modalId, JsonElement response, CancellationToken ct = default) => Task.CompletedTask;
        public void EnqueueSystemNote(string text) { }
        public void EnqueueHelpNote(string text) { }
        public void EnqueueTransientDiagnostic(string text) { }
        public void Interrupt() { }
        public object? GetService(Type serviceType) => null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
#pragma warning restore CS0067

    private sealed class InvalidSnapshotInputQueues(AgentInputQueuesSnapshot snapshot) : IAgentInputQueues
    {
        public AgentInputQueuesSnapshot Snapshot => snapshot;
        public IReadOnlyList<IAgentInputQueue> Queues => [];
        public IAgentInputQueue DefaultQueue => throw new NotSupportedException();
        public IAgentInputQueue ImmediateQueue => throw new NotSupportedException();
        public event EventHandler? Changed { add { } remove { } }
        public AgentInputQueueCommandResult CreateQueue(CreateAgentInputQueueRequest request) => throw new NotSupportedException();
        public AgentInputQueueCommandResult DeleteQueue(DeleteAgentInputQueueRequest request) => throw new NotSupportedException();
        public AgentInputQueueCommandResult Enqueue(EnqueueAgentInputRequest request) => throw new NotSupportedException();
        public AgentInputQueueCommandResult Edit(EditAgentInputQueueItemRequest request) => throw new NotSupportedException();
        public AgentInputQueueCommandResult Remove(RemoveAgentInputQueueItemRequest request) => throw new NotSupportedException();
        public AgentInputQueueCommandResult Move(MoveAgentInputQueueItemRequest request) => throw new NotSupportedException();
        public AgentInputQueueCommandResult Configure(ConfigureAgentInputQueueRequest request) => throw new NotSupportedException();
        public Task<AgentInputQueueCommandResult> CreateQueueAsync(CreateAgentInputQueueRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentInputQueueCommandResult> DeleteQueueAsync(DeleteAgentInputQueueRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentInputQueueCommandResult> EnqueueAsync(EnqueueAgentInputRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentInputQueueCommandResult> EditAsync(EditAgentInputQueueItemRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentInputQueueCommandResult> RemoveAsync(RemoveAgentInputQueueItemRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentInputQueueCommandResult> MoveAsync(MoveAgentInputQueueItemRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentInputQueueCommandResult> ConfigureAsync(ConfigureAgentInputQueueRequest request, CancellationToken ct = default) => throw new NotSupportedException();
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

#pragma warning disable CS0067
    private sealed class UnauthorizedOpenProbeChat : IAgentChat
    {
        public bool InformationWasRead { get; private set; }
        public bool WasDisposed { get; private set; }
        public AgentInformation Information
        {
            get
            {
                this.InformationWasRead = true;
                throw new InvalidOperationException("Unauthorized open must not inspect information.");
            }
        }

        public Usage Usage => default;
        public bool IsBusy => false;
        public AgentChatHistoryCollection History { get; } = new();
        public Task HistoryPopulated => Task.CompletedTask;
        public AgentChatRunningItemCollection RunningItems { get; } = new();
        public IAgentInputQueues InputQueues { get; } = new LocalAgentInputQueuesAdapter(new AgentInputQueueManager(), new AgentInputQueue(new AgentInputQueue.Parameters { Priority = 0, Immediacy = AgentInputQueueImmediacy.Immediate }));
        public ReadOnlyObservableCollection<IRunningSubAgent> SubAgents { get; } = new(new System.Collections.ObjectModel.ObservableCollection<IRunningSubAgent>());
        public ReadOnlyObservableCollection<AgentChatModal> Modals { get; } = new(new System.Collections.ObjectModel.ObservableCollection<AgentChatModal>());
        public ISlashCommandRegistry SlashCommands { get; } = new SlashCommandRegistry();
        public event EventHandler? InformationChanged;
        public event EventHandler? ToolsChanged;
        public event EventHandler? UsageChanged;
        public event EventHandler<AgentChatHistoryItem>? TurnCompleted;
        public IReadOnlyList<AgentChatToolItem> GetToolSnapshot() => [];
        public Task SetToolEnabledAsync(string toolId, bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task RespondToModalAsync(string modalId, JsonElement response, CancellationToken ct = default) => Task.CompletedTask;
        public void EnqueueSystemNote(string text) { }
        public void EnqueueHelpNote(string text) { }
        public void EnqueueTransientDiagnostic(string text) { }
        public void Interrupt() { }
        public object? GetService(Type serviceType) => null;
        public ValueTask DisposeAsync()
        {
            this.WasDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
#pragma warning restore CS0067
}
