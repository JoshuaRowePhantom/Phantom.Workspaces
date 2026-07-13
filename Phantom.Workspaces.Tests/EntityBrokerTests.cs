using System.Runtime.CompilerServices;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityBrokerTests
{
    [PhantomAvaloniaFact]
    public async Task CreateInitializedAsync_PopulatesRepositoryForInMemorySource()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            ct);

        var snapshots = await broker.EntityRepository.ExportEntitySnapshotsAsync(ct);

        Assert.NotEmpty(snapshots);
    }

    [PhantomAvaloniaFact]
    public async Task CreateSubscriptionAsync_LoadsBindableEntities()
    {
        var ct = TestContext.Current.CancellationToken;
        var entityId = new EntityId("11111111-1111-1111-1111-111111111111");
        var timestamp = new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-10), "1");
        var snapshot = CreateSnapshot(
            entityId,
            timestamp,
            """
            {
              "entity-id": "11111111-1111-1111-1111-111111111111",
              "entity-types": ["entity", "task"],
              "names": [["loaded-entity"]],
              "display-name": { "default": "Loaded" }
            }
            """);

        var broker = await CreateBrokerAsync(ct);
        await SeedSnapshotAsync(broker, snapshot);

        var entities = await broker.GetEntitiesAsync([entityId], ct);

        var entity = Assert.Single(entities);
        Assert.Equal(entityId, entity.EntityId);
        Assert.Equal("Loaded", entity.DisplayName);
        Assert.Equal("task", entity.EntityType);
    }

    [PhantomAvaloniaFact]
    public async Task RefreshAsync_UpdatesBindableEntityObject()
    {
        var ct = TestContext.Current.CancellationToken;
        var entityId = new EntityId("22222222-2222-2222-2222-222222222222");
        var initialTimestamp = new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-10), "1");
        var initialSnapshot = CreateSnapshot(
            entityId,
            initialTimestamp,
            """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "entity-types": ["entity", "task"],
              "names": [["loaded-entity"]],
              "display-name": { "default": "Loaded" }
            }
            """);

        var refreshedSnapshot = CreateSnapshot(
            entityId,
            new Timestamp(DateTimeOffset.UtcNow, "2"),
            """
            {
              "entity-id": "22222222-2222-2222-2222-222222222222",
              "entity-types": ["entity", "task"],
              "names": [["loaded-entity"]],
              "display-name": { "default": "Updated" }
            }
            """);

        var broker = await CreateBrokerAsync(ct);
        await SeedSnapshotAsync(broker, initialSnapshot);
        var entities = await broker.GetEntitiesAsync([entityId], ct);
        var entity = Assert.Single(entities);
        var previousModifiedTime = entity.ModifiedTime;

        await SeedSnapshotAsync(broker, refreshedSnapshot, entity.ConcurrencyTag);
        await broker.RefreshAsync(ct);

        Assert.Equal("Updated", entity.DisplayName);
        Assert.NotEqual(previousModifiedTime, entity.ModifiedTime);
        Assert.Contains("\"Updated\"", entity.Data?.GetRawText(), StringComparison.Ordinal);
    }

    [PhantomAvaloniaFact]
    public async Task RefreshAsync_SkipsCollectedSubscriptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var entityId = new EntityId("33333333-3333-3333-3333-333333333333");
        var initialTimestamp = new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-10), "1");
        var initialSnapshot = CreateSnapshot(
            entityId,
            initialTimestamp,
            """
            {
              "entity-id": "33333333-3333-3333-3333-333333333333",
              "entity-types": ["entity", "task"],
              "names": [["collected-entity"]],
              "display-name": { "default": "Collected" }
            }
            """);

        var refreshedSnapshot = CreateSnapshot(
            entityId,
            new Timestamp(DateTimeOffset.UtcNow, "2"),
            """
            {
              "entity-id": "33333333-3333-3333-3333-333333333333",
              "entity-types": ["entity", "task"],
              "names": [["collected-entity"]],
              "display-name": { "default": "Updated" }
            }
            """);

        var broker = await CreateBrokerAsync(ct);
        await SeedSnapshotAsync(broker, initialSnapshot);
        var weakEntity = await CreateCollectedEntityAsync(broker, entityId);

        while (IsWeakReferenceAlive(weakEntity))
        {
            ForceGarbageCollection();
            await Task.Yield();
        }

        var snapshots = await broker.EntityRepository.ExportEntitySnapshotsAsync(ct);
        var concurrencyTag = Assert.Contains(entityId, snapshots).ConcurrencyTag;
        await SeedSnapshotAsync(broker, refreshedSnapshot, concurrencyTag);
        await broker.RefreshAsync(ct);
    }

    [PhantomAvaloniaFact]
    public async Task UpdateAsync_UpdatesSubscribedEntityWithoutRefreshAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var entityId = new EntityId("44444444-4444-4444-4444-444444444444");
        var initialTimestamp = new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-10), "1");
        var initialSnapshot = CreateSnapshot(
            entityId,
            initialTimestamp,
            """
            {
              "entity-id": "44444444-4444-4444-4444-444444444444",
              "entity-types": ["entity", "task"],
              "names": [["live-updated-entity"]],
              "display-name": { "default": "Before Update" }
            }
            """);
        var broker = await CreateBrokerAsync(ct);
        await SeedSnapshotAsync(broker, initialSnapshot);
        var entities = await broker.GetEntitiesAsync([entityId], ct);
        var entity = Assert.Single(entities);

        var changedEntityIds = new List<EntityId>();
        broker.Changed += (_, args) => changedEntityIds.AddRange(args.ChangedEntityIds);

        var updateResult = await broker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Update title",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        ConcurrencyTag = entity.ConcurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = null,
                    },
                ],
            },
            ct);

        var entityResult = Assert.Single(updateResult.EntityResults);
        Assert.Equal(UpdateState.Removed, entityResult.UpdateState);
        Assert.True(entity.Deleted);
        Assert.False(entity.CanDeleteEntity);
        Assert.Null(entity.Data);
        Assert.Contains(entityId, changedEntityIds);
    }

    [PhantomAvaloniaFact]
    public async Task SubscribeGetAsync_LoadsInitialResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstId = new EntityId("55555555-5555-5555-5555-555555555555");
        var secondId = new EntityId("66666666-6666-6666-6666-666666666666");
        var broker = await CreateBrokerAsync(ct);
        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                firstId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-2), "1"),
                """
                {
                  "entity-id": "55555555-5555-5555-5555-555555555555",
                  "entity-types": ["entity", "task"],
                  "names": [["subscriptions", "views", "first"]],
                  "display-name": { "default": "First" }
                }
                """));
        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                secondId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-1), "1"),
                """
                {
                  "entity-id": "66666666-6666-6666-6666-666666666666",
                  "entity-types": ["entity", "task"],
                  "names": [["subscriptions", "views", "second"]],
                  "display-name": { "default": "Second" }
                }
                """));

        var subscribedGet = await broker.SubscribeGetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = new EntityName("subscriptions", "views"),
                        EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                    },
                ],
                Timestamps = [null],
            },
            ct);

        var resultIds = subscribedGet.Results.Select(static entity => entity.EntityId).ToArray();
        Assert.Contains(firstId, resultIds);
        Assert.Contains(secondId, resultIds);
    }

    [PhantomAvaloniaFact]
    public async Task RefreshAsync_SubscribedGetRerunsGetAndReplacesResultCollection()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstId = new EntityId("77777777-7777-7777-7777-777777777777");
        var secondId = new EntityId("88888888-8888-8888-8888-888888888888");
        var broker = await CreateBrokerAsync(ct);

        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                firstId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-2), "1"),
                """
                {
                  "entity-id": "77777777-7777-7777-7777-777777777777",
                  "entity-types": ["entity", "task"],
                  "names": [["subscriptions", "views", "first"]],
                  "display-name": { "default": "First" }
                }
                """));

        var subscribedGet = await broker.SubscribeGetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = new EntityName("subscriptions", "views"),
                        EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                    },
                ],
                Timestamps = [null],
            },
            ct);
        Assert.Equal(firstId, Assert.Single(subscribedGet.Results).EntityId);

        var snapshots = await broker.EntityRepository.ExportEntitySnapshotsAsync(ct);
        var firstConcurrencyTag = Assert.Contains(firstId, snapshots).ConcurrencyTag;
        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                firstId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-1), "2"),
                """
                {
                  "entity-id": "77777777-7777-7777-7777-777777777777",
                  "entity-types": ["entity", "task"],
                  "names": [["subscriptions", "other", "first"]],
                  "display-name": { "default": "First (moved)" }
                }
                """),
            firstConcurrencyTag);
        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                secondId,
                new Timestamp(DateTimeOffset.UtcNow, "1"),
                """
                {
                  "entity-id": "88888888-8888-8888-8888-888888888888",
                  "entity-types": ["entity", "task"],
                  "names": [["subscriptions", "views", "second"]],
                  "display-name": { "default": "Second" }
                }
                """));

        await broker.RefreshAsync(ct);

        var resultIds = subscribedGet.Results.Select(static entity => entity.EntityId).ToArray();
        Assert.DoesNotContain(firstId, resultIds);
        Assert.Contains(secondId, resultIds);
    }

    [PhantomAvaloniaFact]
    public async Task RefreshAsync_SubscribedGet_UsesIncrementalCollectionNotifications()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstId = new EntityId("99999999-9999-9999-9999-999999999999");
        var secondId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var broker = await CreateBrokerAsync(ct);

        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                firstId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-2), "1"),
                """
                {
                  "entity-id": "99999999-9999-9999-9999-999999999999",
                  "entity-types": ["entity", "task"],
                  "names": [["subscriptions", "views", "first"]],
                  "display-name": { "default": "First" }
                }
                """));

        var subscribedGet = await broker.SubscribeGetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = new EntityName("subscriptions", "views"),
                        EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                    },
                ],
                Timestamps = [null],
            },
            ct);

        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        subscribedGet.Results.CollectionChanged += (_, args) =>
        {
            actions.Add(args.Action);
        };

        var snapshots = await broker.EntityRepository.ExportEntitySnapshotsAsync(ct);
        var firstConcurrencyTag = Assert.Contains(firstId, snapshots).ConcurrencyTag;
        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                firstId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-1), "2"),
                """
                {
                  "entity-id": "99999999-9999-9999-9999-999999999999",
                  "entity-types": ["entity", "task"],
                  "names": [["subscriptions", "other", "first"]],
                  "display-name": { "default": "First (moved)" }
                }
                """),
            firstConcurrencyTag);
        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                secondId,
                new Timestamp(DateTimeOffset.UtcNow, "1"),
                """
                {
                  "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                  "entity-types": ["entity", "task"],
                  "names": [["subscriptions", "views", "second"]],
                  "display-name": { "default": "Second" }
                }
                """));

        await broker.RefreshAsync(ct);

        Assert.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Remove, actions);
        Assert.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Add, actions);
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, actions);
    }

    [PhantomAvaloniaFact]
    public async Task RefreshAsync_SubscribedGet_DoesNotClearAndRecreateCollection_WhenMembershipUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var entityId = new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var broker = await CreateBrokerAsync(ct);

        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                entityId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-2), "1"),
                """
                {
                  "entity-id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                  "entity-types": ["entity", "task"],
                  "names": [["subscriptions", "views", "stable"]],
                  "display-name": { "default": "Stable" }
                }
                """));

        var subscribedGet = await broker.SubscribeGetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = new EntityName("subscriptions", "views"),
                        EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                    },
                ],
                Timestamps = [null],
            },
            ct);

        var originalItem = Assert.Single(subscribedGet.Results);
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        subscribedGet.Results.CollectionChanged += (_, args) => actions.Add(args.Action);

        var snapshots = await broker.EntityRepository.ExportEntitySnapshotsAsync(ct);
        var concurrencyTag = Assert.Contains(entityId, snapshots).ConcurrencyTag;
        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                entityId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-1), "2"),
                """
                {
                  "entity-id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                  "entity-types": ["entity", "task"],
                  "names": [["subscriptions", "views", "stable"]],
                  "display-name": { "default": "Stable (updated)" }
                }
                """),
            concurrencyTag);

        await broker.RefreshAsync(ct);

        Assert.Empty(actions);
        Assert.Same(originalItem, Assert.Single(subscribedGet.Results));
        Assert.Equal("Stable (updated)", subscribedGet.Results[0].DisplayName);
    }

    [PhantomAvaloniaFact]
    public async Task SubscribeQueryAsync_ReturnsActionableInterestTargetsForUser()
    {
        var ct = TestContext.Current.CancellationToken;
        var taskId = new EntityId("c1c1c1c1-0000-0000-0000-000000000001");
        var userId = new EntityId("c2c2c2c2-0000-0000-0000-000000000002");
        var relationshipId = new EntityId("c3c3c3c3-0000-0000-0000-000000000003");
        var broker = await CreateBrokerAsync(ct);

        await SeedSnapshotAsync(broker, CreateSnapshot(
            taskId,
            new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-3), "1"),
            """
            {
              "entity-id": "c1c1c1c1-0000-0000-0000-000000000001",
              "entity-types": ["entity", "task"],
              "names": [["tasks", "actionable-one"]],
              "display-name": { "default": "Actionable Task" }
            }
            """));
        await SeedSnapshotAsync(broker, CreateSnapshot(
            userId,
            new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-3), "1"),
            """
            {
              "entity-id": "c2c2c2c2-0000-0000-0000-000000000002",
              "entity-types": ["entity", "user"],
              "names": [["users", "current", "one"]],
              "display-name": { "default": "Current User" }
            }
            """));
        await SeedSnapshotAsync(broker, CreateSnapshot(
            relationshipId,
            new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-2), "1"),
            """
            {
              "entity-id": "c3c3c3c3-0000-0000-0000-000000000003",
              "entity-types": ["entity", "actionable", "relationship"],
              "participants": {
                "target": "c1c1c1c1-0000-0000-0000-000000000001",
                "user": "c2c2c2c2-0000-0000-0000-000000000002"
              }
            }
            """));

        var subscribedQuery = await broker.SubscribeQueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("actionable"),
                        Clause = new EntityParticipationQueryClause
                        {
                            RelationshipTypeNames = new RelationshipTypeNameSet(["actionable"]),
                            ParticipationRoleNames = new RoleNameSet(["target"]),
                            MustHave = new EntityParticipationRequirement
                            {
                                ParticipationRoleNames = new RoleNameSet(["user"]),
                                Clause = new EntityFieldQueryClause
                                {
                                    FieldPath = new FieldPath("entity-id"),
                                    ComparisonOperator = FieldComparisonOperator.Equals,
                                    Value = JsonDocument.Parse("\"c2c2c2c2-0000-0000-0000-000000000002\"").RootElement.Clone(),
                                },
                            },
                        },
                    },
                ],
            },
            ct);

        Assert.Equal(taskId, Assert.Single(subscribedQuery.Results).EntityId);
    }

    [PhantomAvaloniaFact]
    public async Task SubscribeQueryAsync_RefreshesAutomaticallyWhenMatchingEntityAdded()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await CreateBrokerAsync(ct);

        // Create initial session entity via UpdateAsync so broker tracks changes
        var sessionId1 = new EntityId("e1e1e1e1-0000-0000-0000-000000000001");
        var updateResult1 = await broker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Add first session.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = sessionId1,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = JsonDocument.Parse("""
                            {
                              "entity-id": "e1e1e1e1-0000-0000-0000-000000000001",
                              "entity-types": ["entity", "agent-session"],
                              "names": [["test-sessions", "session-1"]],
                              "agent-session-id": "session-1"
                            }
                            """).RootElement.Clone(),
                    },
                ],
            },
            ct);

        // Verify entity was created
        var firstResult = updateResult1.EntityResults.FirstOrDefault(r => r.RequestedEntityId == sessionId1);
        Assert.NotNull(firstResult);
        if (firstResult.UpdateState == UpdateState.Failed)
        {
            Assert.Fail($"Entity creation failed. Errors: {string.Join("; ", firstResult.Errors.Select(e => e.Message))}");
        }

        Assert.Equal(UpdateState.Added, firstResult.UpdateState);
        Assert.NotNull(firstResult.CurrentEntity);

        // Subscribe to agent-session entity-type query
        var subscribedQuery = await broker.SubscribeQueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("sessions"),
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet(["agent-session"]),
                        },
                    },
                ],
            },
            ct);

        // Should have 1 result initially
        Assert.Single(subscribedQuery.Results);

        // Add a second matching entity via UpdateAsync
        var sessionId2 = new EntityId("e2e2e2e2-0000-0000-0000-000000000002");
        await broker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Add second session.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = sessionId2,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = JsonDocument.Parse("""
                            {
                              "entity-id": "e2e2e2e2-0000-0000-0000-000000000002",
                              "entity-types": ["entity", "agent-session"],
                              "names": [["test-sessions", "session-2"]],
                              "agent-session-id": "session-2"
                            }
                            """).RootElement.Clone(),
                    },
                ],
            },
            ct);

        // Query should now have 2 results after automatic refresh
        Assert.Equal(2, subscribedQuery.Results.Count);
        Assert.Contains(subscribedQuery.Results, e => e.EntityId == sessionId1);
        Assert.Contains(subscribedQuery.Results, e => e.EntityId == sessionId2);
    }

    [PhantomAvaloniaFact]
    public async Task SubscribeQueryAsync_RefreshesAutomaticallyWhenMatchingEntityDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await CreateBrokerAsync(ct);

        // Create two session entities via UpdateAsync
        var sessionId1 = new EntityId("f1f1f1f1-0000-0000-0000-000000000001");
        var sessionId2 = new EntityId("f2f2f2f2-0000-0000-0000-000000000002");
        await broker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Add sessions.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = sessionId1,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = JsonDocument.Parse("""
                            {
                              "entity-id": "f1f1f1f1-0000-0000-0000-000000000001",
                              "entity-types": ["entity", "agent-session"],
                              "names": [["test-sessions", "session-1"]],
                              "agent-session-id": "session-1"
                            }
                            """).RootElement.Clone(),
                    },
                    new EntityChange
                    {
                        EntityId = sessionId2,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = JsonDocument.Parse("""
                            {
                              "entity-id": "f2f2f2f2-0000-0000-0000-000000000002",
                              "entity-types": ["entity", "agent-session"],
                              "names": [["test-sessions", "session-2"]],
                              "agent-session-id": "session-2"
                            }
                            """).RootElement.Clone(),
                    },
                ],
            },
            ct);

        // Subscribe to agent-session entity-type query
        var subscribedQuery = await broker.SubscribeQueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("sessions"),
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet(["agent-session"]),
                        },
                    },
                ],
            },
            ct);

        // Should have 2 results initially
        Assert.Equal(2, subscribedQuery.Results.Count);

        // Delete one entity via UpdateAsync
        var entity1 = subscribedQuery.Results.First(e => e.EntityId == sessionId1);
        await broker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Delete session 1.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = sessionId1,
                        ConcurrencyTag = entity1.Snapshot.ConcurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = null,
                    },
                ],
            },
            ct);

        // Query should now have 1 result after automatic refresh
        var remaining = Assert.Single(subscribedQuery.Results);
        Assert.Equal(sessionId2, remaining.EntityId);
    }

    [PhantomAvaloniaFact]
    public async Task RefreshAsync_SkipsCollectedSubscribedGet()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await CreateBrokerAsync(ct);

        var request = new GetRequest
        {
            Entities =
            [
                new GetEntityRequest
                {
                    EntityName = new EntityName("gc-test", "get"),
                    EnumerateChildren = EnumerateChildrenAction.EnumerateSelf,
                },
            ],
            Timestamps = [null],
        };

        var weakRef = await CreateCollectedSubscribedGetAsync(broker, request, ct);

        await Task.Yield(); // flush inline async state machines off the call stack before GC
        while (IsWeakReferenceAlive(weakRef))
        {
            ForceGarbageCollection();
            await Task.Yield();
        }

        await broker.RefreshAsync(ct);

        Assert.Equal(0, broker.ActiveSubscribedGetCount);
    }

    [PhantomAvaloniaFact]
    public async Task RefreshAsync_SkipsCollectedSubscribedQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await CreateBrokerAsync(ct);

        var request = new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("gc-test"),
                    Clause = new EntityTypeQueryClause
                    {
                        EntityTypeNames = new EntityTypeNameSet(["gc-test-type"]),
                    },
                },
            ],
        };

        var weakRef = await CreateCollectedSubscribedQueryAsync(broker, request, ct);

        await Task.Yield(); // flush inline async state machines off the call stack before GC
        while (IsWeakReferenceAlive(weakRef))
        {
            ForceGarbageCollection();
            await Task.Yield();
        }

        await broker.RefreshAsync(ct);

        Assert.Equal(0, broker.ActiveSubscribedQueryCount);
    }

    private static Task<EntityBroker> CreateBrokerAsync(CancellationToken cancellationToken)
    {
        return EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<SubscribedEntityViewModel>> CreateCollectedEntityAsync(
        EntityBroker broker,
        EntityId entityId)
    {
        var entities = await broker.GetEntitiesAsync([entityId], TestContext.Current.CancellationToken);
        var entity = Assert.Single(entities);
        return new WeakReference<SubscribedEntityViewModel>(entity);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<SubscribedGet>> CreateCollectedSubscribedGetAsync(
        EntityBroker broker,
        GetRequest request,
        CancellationToken cancellationToken)
    {
        var sub = await broker.SubscribeGetAsync(request, cancellationToken);
        return new WeakReference<SubscribedGet>(sub);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<SubscribedQuery>> CreateCollectedSubscribedQueryAsync(
        EntityBroker broker,
        QueryRequest request,
        CancellationToken cancellationToken)
    {
        var sub = await broker.SubscribeQueryAsync(request, cancellationToken);
        return new WeakReference<SubscribedQuery>(sub);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsWeakReferenceAlive<T>(WeakReference<T> weakRef) where T : class
        => weakRef.TryGetTarget(out _);

    private static void ForceGarbageCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task SeedSnapshotAsync(
        EntityBroker broker,
        EntitySnapshot snapshot,
        ConcurrencyTag? concurrencyTag = null)
    {
        await broker.EntityRepository.DataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Seed entity broker test snapshot.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = snapshot.EntityId,
                        ConcurrencyTag = concurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = snapshot.Data?.Clone(),
                    },
                ],
            },
            TestContext.Current.CancellationToken);
    }

    [PhantomAvaloniaFact]
    public async Task GetEntitiesAsync_WithDuplicateEntityIdAcrossBatches_DoesNotThrowAndReturnsEntityOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var entityId = new EntityId("d1d1d1d1-0000-0000-0000-000000000001");
        var timestamp = new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-10), "1");
        var snapshot = CreateSnapshot(
            entityId,
            timestamp,
            """
            {
              "entity-id": "d1d1d1d1-0000-0000-0000-000000000001",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [["dup-test", "alias-one"], ["dup-test", "alias-two"]],
              "display-name": { "default": "Worktree" }
            }
            """);

        var broker = await CreateBrokerAsync(ct);
        await SeedSnapshotAsync(broker, snapshot);

        // Two requests using the two different name aliases — both resolve to the same entity,
        // which produces two response batches containing the same EntityId.
        var entities = await broker.GetEntitiesAsync(
            [
                new GetEntityRequest { EntityName = new EntityName("dup-test", "alias-one") },
                new GetEntityRequest { EntityName = new EntityName("dup-test", "alias-two") },
            ],
            ct);

        var entity = Assert.Single(entities);
        Assert.Equal(entityId, entity.EntityId);
    }

    private static EntitySnapshot CreateSnapshot(
        EntityId entityId,
        Timestamp modifiedTime,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        return new EntitySnapshot
        {
            EntityId = entityId,
            ConcurrencyTag = new ConcurrencyTag(modifiedTime.ChangeId),
            ModifiedTime = modifiedTime,
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
    }

    private sealed class CallbackDataAccessLayer : IDataAccessLayer
    {
        private readonly IDataAccessLayer inner;
        private readonly Action? onUpdateAsync;
        private readonly Action? onGetAsync;
        private readonly Action? onQueryAsync;
        private readonly Action? onGetChangedEntitiesAsync;
        private readonly Action<CancellationToken>? captureUpdateToken;
        private readonly Action<CancellationToken>? captureGetToken;
        private readonly Action<CancellationToken>? captureQueryToken;
        private readonly Action<CancellationToken>? captureGetChangedEntitiesToken;

        public CallbackDataAccessLayer(
            IDataAccessLayer inner,
            Action? onUpdateAsync = null,
            Action? onGetAsync = null,
            Action? onQueryAsync = null,
            Action? onGetChangedEntitiesAsync = null,
            Action<CancellationToken>? captureUpdateToken = null,
            Action<CancellationToken>? captureGetToken = null,
            Action<CancellationToken>? captureQueryToken = null,
            Action<CancellationToken>? captureGetChangedEntitiesToken = null)
        {
            this.inner = inner;
            this.onUpdateAsync = onUpdateAsync;
            this.onGetAsync = onGetAsync;
            this.onQueryAsync = onQueryAsync;
            this.onGetChangedEntitiesAsync = onGetChangedEntitiesAsync;
            this.captureUpdateToken = captureUpdateToken;
            this.captureGetToken = captureGetToken;
            this.captureQueryToken = captureQueryToken;
            this.captureGetChangedEntitiesToken = captureGetChangedEntitiesToken;
        }

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
        {
            this.onUpdateAsync?.Invoke();
            this.captureUpdateToken?.Invoke(cancellationToken);
            return this.inner.UpdateAsync(request, cancellationToken);
        }

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            this.onGetAsync?.Invoke();
            this.captureGetToken?.Invoke(cancellationToken);
            return this.inner.GetAsync(request, cancellationToken);
        }

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        {
            this.onQueryAsync?.Invoke();
            this.captureQueryToken?.Invoke(cancellationToken);
            return this.inner.QueryAsync(request, cancellationToken);
        }

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
        {
            this.onGetChangedEntitiesAsync?.Invoke();
            this.captureGetChangedEntitiesToken?.Invoke(cancellationToken);
            return this.inner.GetChangedEntitiesAsync(request, cancellationToken);
        }

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => this.inner.GetHistoryAsync(request, cancellationToken);

#pragma warning disable CS0618 // Type or member is obsolete
        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => this.inner.ExportAsync(request, cancellationToken);
#pragma warning restore CS0618 // Type or member is obsolete

        public Task<ProcessQueueResult> ProcessQueueAsync(ProcessQueueRequest request, CancellationToken cancellationToken = default)
            => this.inner.ProcessQueueAsync(request, cancellationToken);

        public Task<ComputeEmbeddingsResult> ComputeEmbeddingsAsync(ComputeEmbeddingsRequest request, CancellationToken cancellationToken = default)
            => this.inner.ComputeEmbeddingsAsync(request, cancellationToken);

        public Task<UpdateEmbeddingsResult> UpdateEmbeddingsAsync(UpdateEmbeddingsRequest request, CancellationToken cancellationToken = default)
            => this.inner.UpdateEmbeddingsAsync(request, cancellationToken);
    }

    [PhantomAvaloniaFact]
    public async Task UpdateAsync_DoesNotBlockCallingThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await CreateBrokerAsync(ct);

        // This test verifies that UpdateAsync doesn't synchronously block.
        // If the DAL call runs inline on the calling thread, long DAL operations
        // would block the UI thread. By wrapping in Task.Run, the operation
        // is dispatched to the thread pool, keeping the calling thread responsive.

        var updateResult = await broker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Test update" },
                },
                Changes = [],
            },
            ct);

        Assert.NotNull(updateResult);
    }

    [PhantomAvaloniaFact]
    public async Task RefreshAsync_DoesNotBlockCallingThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var entityId = new EntityId("55555555-5555-5555-5555-555555555555");
        var snapshot = CreateSnapshot(
            entityId,
            new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-10), "1"),
            """
            {
              "entity-id": "55555555-5555-5555-5555-555555555555",
              "entity-types": ["entity", "task"],
              "names": [["test-entity"]],
              "display-name": { "default": "Test" }
            }
            """);

        var broker = await CreateBrokerAsync(ct);
        await SeedSnapshotAsync(broker, snapshot);

        var entities = await broker.GetEntitiesAsync([entityId], ct);
        Assert.Single(entities);

        // This test verifies that RefreshAsync doesn't synchronously block.
        // If GetChangedEntitiesAsync runs inline on the calling thread, long DAL
        // operations would block the UI thread.

        await broker.RefreshAsync(ct);
    }

    [PhantomAvaloniaFact]
    public async Task GetEntitiesAsync_DoesNotBlockCallingThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var entityId = new EntityId("77777777-7777-7777-7777-777777777777");
        var snapshot = CreateSnapshot(
            entityId,
            new Timestamp(DateTimeOffset.UtcNow, "1"),
            """
            {
              "entity-id": "77777777-7777-7777-7777-777777777777",
              "entity-types": ["entity", "task"],
              "names": [["get-test"]],
              "display-name": { "default": "Get Test" }
            }
            """);

        var broker = await CreateBrokerAsync(ct);
        await SeedSnapshotAsync(broker, snapshot);

        // This test verifies that GetEntitiesAsync (which calls
        // GetSubscribedEntitiesForGetRequestAsync) doesn't synchronously block.
        // If GetAsync runs inline on the calling thread, long DAL operations
        // would block the UI thread.

        var entities = await broker.GetEntitiesAsync([entityId], ct);

        Assert.Single(entities);
    }

    [PhantomAvaloniaFact]
    public async Task SubscribeQueryAsync_DoesNotBlockCallingThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await CreateBrokerAsync(ct);

        // This test verifies that SubscribeQueryAsync (which calls
        // GetSubscribedEntitiesForQueryRequestAsync) doesn't synchronously block.
        // If QueryAsync runs inline on the calling thread, long DAL operations
        // would block the UI thread.

        var subscription = await broker.SubscribeQueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("test-query"),
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet(["entity"]),
                        },
                    },
                ],
            },
            ct);

        Assert.NotNull(subscription);
    }

    [PhantomAvaloniaFact]
    public async Task UpdateAsync_RunsOnThreadPoolThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await CreateBrokerAsync(ct);

        int? dalThreadId = null;
        int callingThreadId = Environment.CurrentManagedThreadId;

        var mockDal = new CallbackDataAccessLayer(
            broker.EntityRepository.DataAccessLayer,
            onUpdateAsync: () => dalThreadId = Environment.CurrentManagedThreadId);

        broker.EntityRepository.SetDataAccessLayerForTesting(mockDal);

        await broker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Test" },
                },
                Changes = [],
            },
            ct);

        Assert.NotNull(dalThreadId);
        Assert.NotEqual(callingThreadId, dalThreadId.Value);
    }

    [PhantomAvaloniaFact]
    public async Task RefreshAsync_RunsOnThreadPoolThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var entityId = new EntityId("88888888-8888-8888-8888-888888888888");
        var broker = await CreateBrokerAsync(ct);

        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                entityId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-10), "1"),
                """
                {
                  "entity-id": "88888888-8888-8888-8888-888888888888",
                  "entity-types": ["entity", "task"],
                  "names": [["test-refresh-thread"]],
                  "display-name": { "default": "Test" }
                }
                """));

        await broker.GetEntitiesAsync([entityId], ct);

        int? dalThreadId = null;
        int callingThreadId = Environment.CurrentManagedThreadId;

        var mockDal = new CallbackDataAccessLayer(
            broker.EntityRepository.DataAccessLayer,
            onGetChangedEntitiesAsync: () => dalThreadId = Environment.CurrentManagedThreadId);

        broker.EntityRepository.SetDataAccessLayerForTesting(mockDal);

        await broker.RefreshAsync(ct);

        Assert.NotNull(dalThreadId);
        Assert.NotEqual(callingThreadId, dalThreadId.Value);
    }

    [PhantomAvaloniaFact]
    public async Task GetSubscribedEntitiesForGetRequest_RunsOnThreadPoolThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await CreateBrokerAsync(ct);

        int? dalThreadId = null;
        int callingThreadId = Environment.CurrentManagedThreadId;

        var mockDal = new CallbackDataAccessLayer(
            broker.EntityRepository.DataAccessLayer,
            onGetAsync: () => dalThreadId = Environment.CurrentManagedThreadId);

        broker.EntityRepository.SetDataAccessLayerForTesting(mockDal);

        await broker.SubscribeGetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityName = new EntityName("test") }],
                Timestamps = [null],
            },
            ct);

        Assert.NotNull(dalThreadId);
        Assert.NotEqual(callingThreadId, dalThreadId.Value);
    }

    [PhantomAvaloniaFact]
    public async Task GetSubscribedEntitiesForQueryRequest_RunsOnThreadPoolThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await CreateBrokerAsync(ct);

        int? dalThreadId = null;
        int callingThreadId = Environment.CurrentManagedThreadId;

        var mockDal = new CallbackDataAccessLayer(
            broker.EntityRepository.DataAccessLayer,
            onQueryAsync: () => dalThreadId = Environment.CurrentManagedThreadId);

        broker.EntityRepository.SetDataAccessLayerForTesting(mockDal);

        await broker.SubscribeQueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("test"),
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet(["entity"]),
                        },
                    },
                ],
            },
            ct);

        Assert.NotNull(dalThreadId);
        Assert.NotEqual(callingThreadId, dalThreadId.Value);
    }

    [PhantomAvaloniaFact]
    public async Task LoadSnapshots_RunsOnThreadPoolThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var entityId = new EntityId("99999999-9999-9999-9999-999999999999");
        var broker = await CreateBrokerAsync(ct);

        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                entityId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-10), "1"),
                """
                {
                  "entity-id": "99999999-9999-9999-9999-999999999999",
                  "entity-types": ["entity", "task"],
                  "names": [["test-load"]],
                  "display-name": { "default": "Test" }
                }
                """));

        int? dalThreadId = null;
        int callingThreadId = Environment.CurrentManagedThreadId;

        var mockDal = new CallbackDataAccessLayer(
            broker.EntityRepository.DataAccessLayer,
            onGetAsync: () => dalThreadId = Environment.CurrentManagedThreadId);

        broker.EntityRepository.SetDataAccessLayerForTesting(mockDal);

        await broker.GetEntitiesAsync([entityId], ct);

        Assert.NotNull(dalThreadId);
        Assert.NotEqual(callingThreadId, dalThreadId.Value);
    }

    [PhantomAvaloniaFact]
    public async Task UpdateAsync_ForwardsCancellationToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await CreateBrokerAsync(ct);

        var cts = new CancellationTokenSource();
        var testToken = cts.Token;
        CancellationToken? receivedToken = null;

        var mockDal = new CallbackDataAccessLayer(
            broker.EntityRepository.DataAccessLayer,
            onUpdateAsync: () => { },
            captureUpdateToken: token => receivedToken = token);

        broker.EntityRepository.SetDataAccessLayerForTesting(mockDal);

        await broker.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Test" },
                },
                Changes = [],
            },
            testToken);

        Assert.NotNull(receivedToken);
        Assert.Equal(testToken, receivedToken.Value);
    }

    [PhantomAvaloniaFact]
    public async Task RefreshAsync_ForwardsCancellationToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var entityId = new EntityId("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var broker = await CreateBrokerAsync(ct);

        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                entityId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-10), "1"),
                """
                {
                  "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                  "entity-types": ["entity", "task"],
                  "names": [["test-refresh-token"]],
                  "display-name": { "default": "Test" }
                }
                """));

        await broker.GetEntitiesAsync([entityId], ct);

        var cts = new CancellationTokenSource();
        var testToken = cts.Token;
        CancellationToken? receivedToken = null;

        var mockDal = new CallbackDataAccessLayer(
            broker.EntityRepository.DataAccessLayer,
            onGetChangedEntitiesAsync: () => { },
            captureGetChangedEntitiesToken: token => receivedToken = token);

        broker.EntityRepository.SetDataAccessLayerForTesting(mockDal);

        await broker.RefreshAsync(testToken);

        Assert.NotNull(receivedToken);
        Assert.Equal(testToken, receivedToken.Value);
    }

    [PhantomAvaloniaFact]
    public async Task GetSubscribedEntitiesForGetRequest_ForwardsCancellationToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await CreateBrokerAsync(ct);

        var cts = new CancellationTokenSource();
        var testToken = cts.Token;
        CancellationToken? receivedToken = null;

        var mockDal = new CallbackDataAccessLayer(
            broker.EntityRepository.DataAccessLayer,
            onGetAsync: () => { },
            captureGetToken: token => receivedToken = token);

        broker.EntityRepository.SetDataAccessLayerForTesting(mockDal);

        await broker.SubscribeGetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityName = new EntityName("test") }],
                Timestamps = [null],
            },
            testToken);

        Assert.NotNull(receivedToken);
        Assert.Equal(testToken, receivedToken.Value);
    }

    [PhantomAvaloniaFact]
    public async Task GetSubscribedEntitiesForQueryRequest_ForwardsCancellationToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await CreateBrokerAsync(ct);

        var cts = new CancellationTokenSource();
        var testToken = cts.Token;
        CancellationToken? receivedToken = null;

        var mockDal = new CallbackDataAccessLayer(
            broker.EntityRepository.DataAccessLayer,
            onQueryAsync: () => { },
            captureQueryToken: token => receivedToken = token);

        broker.EntityRepository.SetDataAccessLayerForTesting(mockDal);

        await broker.SubscribeQueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("test"),
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet(["entity"]),
                        },
                    },
                ],
            },
            testToken);

        Assert.NotNull(receivedToken);
        Assert.Equal(testToken, receivedToken.Value);
    }

    [PhantomAvaloniaFact]
    public async Task LoadSnapshots_ForwardsCancellationToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var entityId = new EntityId("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var broker = await CreateBrokerAsync(ct);

        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                entityId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-10), "1"),
                """
                {
                  "entity-id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                  "entity-types": ["entity", "task"],
                  "names": [["test-load-token"]],
                  "display-name": { "default": "Test" }
                }
                """));

        var cts = new CancellationTokenSource();
        var testToken = cts.Token;
        CancellationToken? receivedToken = null;

        var mockDal = new CallbackDataAccessLayer(
            broker.EntityRepository.DataAccessLayer,
            onGetAsync: () => { },
            captureGetToken: token => receivedToken = token);

        broker.EntityRepository.SetDataAccessLayerForTesting(mockDal);

        await broker.GetEntitiesAsync([entityId], testToken);

        Assert.NotNull(receivedToken);
        Assert.Equal(testToken, receivedToken.Value);
    }
}

