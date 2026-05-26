using System.Runtime.CompilerServices;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityBrokerTests
{
    [Fact]
    public async Task CreateInitializedAsync_PopulatesRepositoryForInMemorySource()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(
            new RepositorySource(RepositorySourceType.Unknown, "(none)"),
            ct);

        var snapshots = await broker.EntityRepository.ExportEntitySnapshotsAsync(ct);

        Assert.NotEmpty(snapshots);
    }

    [Fact]
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
              "entity-types": ["entity"],
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
        Assert.Equal("entity", entity.EntityType);
    }

    [Fact]
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
              "entity-types": ["entity"],
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
              "entity-types": ["entity"],
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

    [Fact]
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
              "entity-types": ["entity"],
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
              "entity-types": ["entity"],
              "names": [["collected-entity"]],
              "display-name": { "default": "Updated" }
            }
            """);

        var broker = await CreateBrokerAsync(ct);
        await SeedSnapshotAsync(broker, initialSnapshot);
        var weakEntity = await CreateCollectedEntityAsync(broker, entityId);

        ForceGarbageCollection();
        Assert.False(weakEntity.TryGetTarget(out _));

        var snapshots = await broker.EntityRepository.ExportEntitySnapshotsAsync(ct);
        var concurrencyTag = Assert.Contains(entityId, snapshots).ConcurrencyTag;
        await SeedSnapshotAsync(broker, refreshedSnapshot, concurrencyTag);
        await broker.RefreshAsync(ct);
    }

    [Fact]
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
              "entity-types": ["entity"],
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
        Assert.Null(entity.Data);
        Assert.Contains(entityId, changedEntityIds);
    }

    [Fact]
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
                  "entity-types": ["entity"],
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
                  "entity-types": ["entity"],
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

    [Fact]
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
                  "entity-types": ["entity"],
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
                  "entity-types": ["entity"],
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
                  "entity-types": ["entity"],
                  "names": [["subscriptions", "views", "second"]],
                  "display-name": { "default": "Second" }
                }
                """));

        await broker.RefreshAsync(ct);

        var resultIds = subscribedGet.Results.Select(static entity => entity.EntityId).ToArray();
        Assert.DoesNotContain(firstId, resultIds);
        Assert.Contains(secondId, resultIds);
    }

    [Fact]
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
                  "entity-types": ["entity"],
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
                  "entity-types": ["entity"],
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
                  "entity-types": ["entity"],
                  "names": [["subscriptions", "views", "second"]],
                  "display-name": { "default": "Second" }
                }
                """));

        await broker.RefreshAsync(ct);

        Assert.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Remove, actions);
        Assert.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Add, actions);
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, actions);
    }

    [Fact]
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
                  "entity-types": ["entity"],
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
                  "entity-types": ["entity"],
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

    private static Task<EntityBroker> CreateBrokerAsync(CancellationToken cancellationToken)
    {
        return EntityBroker.CreateInitializedAsync(
            new RepositorySource(RepositorySourceType.Unknown, "(none)"),
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
}
