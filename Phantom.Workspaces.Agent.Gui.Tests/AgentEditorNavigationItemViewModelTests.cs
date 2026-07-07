using System.ComponentModel;
using Phantom.Workspaces.Agent.Gui.ViewModels;

namespace Phantom.Workspaces.Agent.Gui.Tests;

public sealed class AgentEditorNavigationItemViewModelTests
{
    [Fact]
    public void HasChildren_RaisesPropertyChanged_WhenChildrenAdded()
    {
        var navItem = new AgentEditorNavigationItemViewModel(
            "test-id",
            "Test Item",
            null,
            null,
            null,
            new object(),
            []);

        Assert.False(navItem.HasChildren);

        var propertyChangedEvents = new List<string?>();
        navItem.PropertyChanged += (_, e) => propertyChangedEvents.Add(e.PropertyName);

        navItem.Children.Add(new AgentEditorNavigationItemViewModel(
            "child-1",
            "Child 1",
            null,
            null,
            null,
            new object(),
            []));

        Assert.Contains(nameof(navItem.HasChildren), propertyChangedEvents);
        Assert.True(navItem.HasChildren);
    }

    [Fact]
    public void NotHasChildren_RaisesPropertyChanged_WhenChildrenAdded()
    {
        var navItem = new AgentEditorNavigationItemViewModel(
            "test-id",
            "Test Item",
            null,
            null,
            null,
            new object(),
            []);

        Assert.True(navItem.NotHasChildren);

        var propertyChangedEvents = new List<string?>();
        navItem.PropertyChanged += (_, e) => propertyChangedEvents.Add(e.PropertyName);

        navItem.Children.Add(new AgentEditorNavigationItemViewModel(
            "child-1",
            "Child 1",
            null,
            null,
            null,
            new object(),
            []));

        Assert.Contains(nameof(navItem.NotHasChildren), propertyChangedEvents);
        Assert.False(navItem.NotHasChildren);
    }

    [Fact]
    public void ToggleExpandCommand_CanExecute_UpdatesWhenChildrenAdded()
    {
        var navItem = new AgentEditorNavigationItemViewModel(
            "test-id",
            "Test Item",
            null,
            null,
            null,
            new object(),
            []);

        Assert.False(navItem.ToggleExpandCommand.CanExecute(null));

        var canExecuteChangedRaised = false;
        navItem.ToggleExpandCommand.CanExecuteChanged += (_, _) => canExecuteChangedRaised = true;

        navItem.Children.Add(new AgentEditorNavigationItemViewModel(
            "child-1",
            "Child 1",
            null,
            null,
            null,
            new object(),
            []));

        Assert.True(canExecuteChangedRaised);
        Assert.True(navItem.ToggleExpandCommand.CanExecute(null));
    }
}
