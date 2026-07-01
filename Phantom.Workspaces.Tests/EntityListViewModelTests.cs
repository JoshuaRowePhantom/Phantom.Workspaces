using Avalonia;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityListViewModelTests
{
    [PhantomAvaloniaFact]
    public void SetItems_OrdersByOrderAndPreservesHierarchyLevel()
    {
        var list = new EntityListViewModel();
        var first = new EntityListNodeViewModel(
            displayName: "First",
            entityType: "folder",
            nameComponents: ["first"],
            sortKey: "[\"first\"]");
        var second = new EntityListNodeViewModel(
            displayName: "Second",
            entityType: "entity",
            nameComponents: ["second"],
            sortKey: "[\"second\"]");

        list.SetItems(
        [
            new EntityListItemViewModel(second, order: 2, level: 1, itemKey: "[\"second\"]", parentItemKey: "[\"first\"]"),
            new EntityListItemViewModel(first, order: 1, level: 0, itemKey: "[\"first\"]", childItemKeys: ["[\"second\"]"], isExpanded: true),
        ]);

        Assert.Equal(2, list.Items.Count);
        Assert.Same(first, list.Items[0].Node);
        Assert.Equal(0, list.Items[0].Level);
        Assert.Same(second, list.Items[1].Node);
        Assert.Equal(1, list.Items[1].Level);
        Assert.Equal("[\"first\"]", list.Items[1].ParentItemKey);
        Assert.True(list.Items[0].IsExpanded);
    }

    [PhantomAvaloniaFact]
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

    [PhantomAvaloniaFact]
    public void EntityListNodeViewModel_ToggleExpandCommand_TogglesExpansionState()
    {
        var parent = new EntityListNodeViewModel(
            displayName: "Parent",
            entityType: "folder",
            nameComponents: ["parent"],
            sortKey: "[\"parent\"]");
        var child = new EntityListNodeViewModel(
            displayName: "Child",
            entityType: "entity",
            nameComponents: ["parent", "child"],
            sortKey: "[\"parent\",\"child\"]");
        parent.SetChildren([child]);

        // Initially collapsed
        Assert.False(parent.IsExpanded);
        Assert.Empty(parent.VisibleChildren);
        Assert.Equal("▾", parent.ExpandArrow);
        Assert.True(parent.ToggleExpandCommand.CanExecute(null));

        // Execute command to expand
        parent.ToggleExpandCommand.Execute(null);
        Assert.True(parent.IsExpanded);
        Assert.Single(parent.VisibleChildren);
        Assert.Same(child, parent.VisibleChildren[0]);
        Assert.Equal("▴", parent.ExpandArrow);

        // Execute command to collapse
        parent.ToggleExpandCommand.Execute(null);
        Assert.False(parent.IsExpanded);
        Assert.Empty(parent.VisibleChildren);
        Assert.Equal("▾", parent.ExpandArrow);
    }

    [PhantomAvaloniaFact]
    public void EntityListNodeViewModel_ToggleExpandCommand_DisabledWhenNoChildren()
    {
        var node = new EntityListNodeViewModel(
            displayName: "Leaf",
            entityType: "entity",
            nameComponents: ["leaf"],
            sortKey: "[\"leaf\"]");

        Assert.False(node.HasChildren);
        Assert.False(node.ToggleExpandCommand.CanExecute(null));
    }

    [PhantomAvaloniaFact]
    public void EntityListNodeViewModel_SetChildren_EnablesToggleExpandCommand()
    {
        var parent = new EntityListNodeViewModel(
            displayName: "Parent",
            entityType: "folder",
            nameComponents: ["parent"],
            sortKey: "[\"parent\"]");

        // Initially no children
        Assert.False(parent.HasChildren);
        Assert.False(parent.ToggleExpandCommand.CanExecute(null));

        // Add child
        var child = new EntityListNodeViewModel(
            displayName: "Child",
            entityType: "entity",
            nameComponents: ["parent", "child"],
            sortKey: "[\"parent\",\"child\"]");
        parent.SetChildren([child]);

        // Now has children and command is enabled
        Assert.True(parent.HasChildren);
        Assert.True(parent.ToggleExpandCommand.CanExecute(null));
    }

}
