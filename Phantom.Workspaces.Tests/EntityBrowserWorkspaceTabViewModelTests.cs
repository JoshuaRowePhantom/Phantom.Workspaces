using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityBrowserWorkspaceTabViewModelTests
{
    [Fact]
    public async Task BrowserList_TracksParentChildMetadataAndExpansion()
    {
        var broker = await CreateBrokerAsync();

        var parentId = new EntityId("11111111-1111-1111-1111-111111111111");
        var childId = new EntityId("22222222-2222-2222-2222-222222222222");
        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                parentId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-2), "1"),
                """
                {
                  "entity-id": "11111111-1111-1111-1111-111111111111",
                  "entity-types": ["folder"],
                  "names": [["entity-types"]],
                  "display-name": { "default": "Entity Types" }
                }
                """));
        await SeedSnapshotAsync(
            broker,
            CreateSnapshot(
                childId,
                new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-1), "1"),
                """
                {
                  "entity-id": "22222222-2222-2222-2222-222222222222",
                  "entity-types": ["entity-type"],
                  "names": [["entity-types", "workspace"]],
                  "display-name": { "default": "Workspace" }
                }
                """));

        var rootSubscription = await broker.SubscribeGetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = EntityName.Root,
                        EnumerateChildren = EnumerateChildrenAction.EnumerateSelf,
                    },
                    new GetEntityRequest
                    {
                        EntityName = EntityName.Root,
                        EnumerateChildren = EnumerateChildrenAction.EnumerateChildren,
                    },
                ],
                Timestamps = [null],
            },
            TestContext.Current.CancellationToken);
        var viewModel = new EntityBrowserWorkspaceTabViewModel(broker, rootSubscription)
        {
            Id = "entity-browser-tab",
            Title = "Entity Browser",
        };

        await WaitForConditionAsync(() =>
            viewModel.EntityList.Items.Any(item =>
                string.Equals(item.ItemKey, "[\"entity-types\"]", StringComparison.Ordinal)
                && item.HasChildren));

        var rootItem = Assert.Single(
            viewModel.EntityList.Items,
            item => string.Equals(item.ItemKey, "[]", StringComparison.Ordinal));
        Assert.True(rootItem.IsExpanded);
        Assert.Equal(0, rootItem.Level);
        Assert.Null(rootItem.ParentItemKey);

        var parentItem = Assert.Single(
            viewModel.EntityList.Items,
            item => string.Equals(item.ItemKey, "[\"entity-types\"]", StringComparison.Ordinal));
        Assert.Equal("[\"entity-types\"]", parentItem.ItemKey);
        Assert.Equal("[]", parentItem.ParentItemKey);
        Assert.Equal(1, parentItem.Level);
        Assert.Contains("[\"entity-types\",\"workspace\"]", parentItem.ChildItemKeys);

        parentItem.IsExpanded = true;
        await WaitForConditionAsync(() =>
            viewModel.EntityList.Items.Any(item =>
                string.Equals(item.ItemKey, "[\"entity-types\",\"workspace\"]", StringComparison.Ordinal)));

        var childItem = Assert.Single(
            viewModel.EntityList.Items,
            item => string.Equals(item.ItemKey, "[\"entity-types\",\"workspace\"]", StringComparison.Ordinal));
        Assert.Equal(2, childItem.Level);
        Assert.Equal(parentItem.ItemKey, childItem.ParentItemKey);

        Assert.Equal(0, rootItem.StickyRow);
        Assert.Equal(1, parentItem.StickyRow);
        Assert.Null(childItem.StickyRow);
    }

    private static Task<EntityBroker> CreateBrokerAsync()
    {
        return EntityBroker.CreateInitializedAsync(
            new RepositorySource(RepositorySourceType.Unknown, "(none)"),
            TestContext.Current.CancellationToken);
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
                        Text = "Seed browser test snapshot.",
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

    private static async Task WaitForConditionAsync(
        Func<bool> condition)
    {
        var ct = TestContext.Current.CancellationToken;
        var timeout = TimeSpan.FromSeconds(5);
        var pollInterval = TimeSpan.FromMilliseconds(25);
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > timeout)
            {
                throw new TimeoutException("Timed out waiting for expected browser state.");
            }

            await Task.Delay(pollInterval, ct);
        }
    }
}
