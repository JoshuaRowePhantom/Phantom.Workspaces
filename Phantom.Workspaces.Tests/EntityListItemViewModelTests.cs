using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityListItemViewModelTests
{
    [AvaloniaFact]
    public void ToggleExpandCommand_UpdatesItemAndNodeExpansionState()
    {
        var node = new EntityListNodeViewModel(
            displayName: "Folder",
            entityType: "folder",
            nameComponents: ["folder"],
            sortKey: "[\"folder\"]");
        var item = new EntityListItemViewModel(
            node,
            order: 0,
            level: 0,
            itemKey: "[\"folder\"]",
            childItemKeys: ["[\"folder\",\"child\"]"],
            isExpanded: false);

        Assert.True(item.ToggleExpandCommand.CanExecute(null));
        Assert.False(item.IsExpanded);
        Assert.False(node.IsExpanded);

        item.ToggleExpandCommand.Execute(null);

        Assert.True(item.IsExpanded);
        Assert.True(node.IsExpanded);
        Assert.Equal("▴", item.ExpandArrow);
    }

    [AvaloniaFact]
    public void ToggleExpandCommand_DisabledWhenNoChildren()
    {
        var node = new EntityListNodeViewModel(
            displayName: "Leaf",
            entityType: "entity",
            nameComponents: ["leaf"],
            sortKey: "[\"leaf\"]");
        var item = new EntityListItemViewModel(
            node,
            order: 0,
            level: 0,
            itemKey: "[\"leaf\"]");

        Assert.False(item.HasChildren);
        Assert.False(item.ToggleExpandCommand.CanExecute(null));
    }
}
