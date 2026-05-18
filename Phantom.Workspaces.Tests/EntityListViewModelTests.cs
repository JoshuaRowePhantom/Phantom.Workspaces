using System.Text.Json;
using Avalonia;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityListViewModelTests
{
    [Fact]
    public void PopulateFromEntities_WithRootNode_BuildsHierarchyFromRoot()
    {
        var entities = new[]
        {
            CreateEntity(
                entityId: "11111111-1111-1111-1111-111111111111",
                namesJson: """[["z"]]""",
                displayName: "Z"),
            CreateEntity(
                entityId: "22222222-2222-2222-2222-222222222222",
                namesJson: """[["a"]]""",
                displayName: "A"),
            CreateEntity(
                entityId: "33333333-3333-3333-3333-333333333333",
                namesJson: """[["a","child"]]""",
                displayName: "A child"),
        };
        var list = new EntityListViewModel();

        list.PopulateFromEntities(entities, includeRootNode: true);

        var root = Assert.Single(list.RootEntities);
        Assert.Equal("Root", root.DisplayName);
        Assert.True(root.IsExpanded);
        Assert.Equal(2, root.VisibleChildren.Count);
        Assert.Equal("A", root.VisibleChildren[0].DisplayName);
        Assert.Equal("Z", root.VisibleChildren[1].DisplayName);
        Assert.Single(root.VisibleChildren[0].Children);
        Assert.Equal("A child", root.VisibleChildren[0].Children[0].DisplayName);
    }

    [Fact]
    public void TreeNode_CornerRadiusAndVisibility_TrackChildExpansionState()
    {
        var parent = new EntityListNodeViewModel(
            displayName: "Parent",
            entityType: "folder",
            nameComponents: ["parent"],
            sortKey: "[\"parent\"]");
        Assert.False(parent.HasChildren);
        Assert.Equal(new CornerRadius(6), parent.ContentCornerRadius);

        var child = new EntityListNodeViewModel(
            displayName: "Child",
            entityType: "folder",
            nameComponents: ["parent", "child"],
            sortKey: "[\"parent\",\"child\"]");
        parent.SetChildren([child]);
        Assert.True(parent.HasChildren);
        Assert.Equal(new CornerRadius(6, 6, 0, 0), parent.ContentCornerRadius);
        Assert.Empty(parent.VisibleChildren);

        parent.IsExpanded = true;
        Assert.Single(parent.VisibleChildren);
        Assert.Equal("▴", parent.ExpandArrow);
    }

    private static SubscribedEntityViewModel CreateEntity(
        string entityId,
        string namesJson,
        string displayName)
    {
        var snapshot = CreateSnapshot(
            entityId,
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity"],
              "names": {{namesJson}},
              "display-name": { "default": "{{displayName}}" }
            }
            """);
        return new SubscribedEntityViewModel(snapshot);
    }

    private static EntitySnapshot CreateSnapshot(
        string entityId,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        return new EntitySnapshot
        {
            EntityId = new EntityId(entityId),
            ConcurrencyTag = new ConcurrencyTag("1"),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = document.RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };
    }
}
