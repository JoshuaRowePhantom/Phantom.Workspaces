using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ViewEntityViewModelTests : IAsyncDisposable
{
    private readonly MainWindowViewModel mainWindowViewModel;

    public ViewEntityViewModelTests()
    {
        this.mainWindowViewModel = new MainWindowViewModel(new UnknownRepositorySource());
    }

    public async ValueTask DisposeAsync()
    {
        await this.mainWindowViewModel.DisposeAsync();
    }

    [Fact]
    public void HasTraversedChildren_DefaultsToFalse()
    {
        var viewModel = this.CreateViewModel();

        Assert.False(viewModel.HasTraversedChildren);
    }

    [Fact]
    public void HasChildren_DefaultsToFalse()
    {
        var viewModel = this.CreateViewModel();

        Assert.False(viewModel.HasChildren);
    }

    [Fact]
    public void NotHasChildren_DefaultsToTrue()
    {
        var viewModel = this.CreateViewModel();

        Assert.True(viewModel.NotHasChildren);
    }

    [Fact]
    public void HasTraversedChildren_CanBeSetToTrue()
    {
        var viewModel = this.CreateViewModel();

        viewModel.HasTraversedChildren = true;

        Assert.True(viewModel.HasTraversedChildren);
    }

    [Fact]
    public void HasChildren_IsTrueAfterAddChild()
    {
        var parent = this.CreateViewModel();
        var child = this.CreateViewModel();

        parent.AddChild(child);

        Assert.True(parent.HasChildren);
        Assert.False(parent.NotHasChildren);
    }

    [Fact]
    public void HasChildren_RaisesPropertyChanged_WhenHasTraversedChildrenChanges()
    {
        var viewModel = this.CreateViewModel();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        viewModel.HasTraversedChildren = true;

        Assert.Contains(nameof(ViewEntityViewModel.HasChildren), changed);
        Assert.Contains(nameof(ViewEntityViewModel.NotHasChildren), changed);
    }

    [Fact]
    public void ToggleExpandCommand_TogglesIsExpanded_FromTrueToFalse()
    {
        var viewModel = this.CreateViewModel(isExpanded: true);

        viewModel.ToggleExpandCommand.Execute(null);

        Assert.False(viewModel.IsExpanded);
    }

    [Fact]
    public void ToggleExpandCommand_TogglesIsExpanded_FromFalseToTrue()
    {
        var viewModel = this.CreateViewModel(isExpanded: false);

        viewModel.ToggleExpandCommand.Execute(null);

        Assert.True(viewModel.IsExpanded);
    }

    [Fact]
    public void ExpandArrow_ReturnsCollapseGlyph_WhenExpanded()
    {
        var viewModel = this.CreateViewModel(isExpanded: true);

        Assert.Equal("▴", viewModel.ExpandArrow);
    }

    [Fact]
    public void ExpandArrow_ReturnsExpandGlyph_WhenCollapsed()
    {
        var viewModel = this.CreateViewModel(isExpanded: false);

        Assert.Equal("▾", viewModel.ExpandArrow);
    }

    [Fact]
    public void ExpandArrow_UpdatesWhenIsExpandedChanges()
    {
        var viewModel = this.CreateViewModel(isExpanded: true);
        Assert.Equal("▴", viewModel.ExpandArrow);

        viewModel.ToggleExpandCommand.Execute(null);

        Assert.Equal("▾", viewModel.ExpandArrow);
    }

    [Fact]
    public void ToggleExpandCommand_DisabledWhenNoTraversedChildren()
    {
        var viewModel = this.CreateViewModel();

        Assert.False(viewModel.ToggleExpandCommand.CanExecute(null));
    }

    [Fact]
    public void ToggleExpandCommand_EnabledWhenHasTraversedChildren()
    {
        var viewModel = this.CreateViewModel();
        viewModel.HasTraversedChildren = true;

        Assert.True(viewModel.ToggleExpandCommand.CanExecute(null));
    }

    [Fact]
    public void Children_ExposedAsObservableCollection_ForTreeView()
    {
        var viewModel = this.CreateViewModel();

        Assert.Empty(viewModel.Children);
    }

    [Fact]
    public void AddChild_AddsNestedEntityAndMarksParent()
    {
        var parent = this.CreateViewModel();
        var child = this.CreateViewModel();

        parent.AddChild(child);

        Assert.Single(parent.Children);
        Assert.Same(child, parent.Children[0]);
        Assert.True(parent.HasTraversedChildren);
        Assert.True(parent.HasChildren);
        Assert.True(child.HasParent);
    }

    [Fact]
    public async Task InitializeAsync_PopulatesShortcuts()
    {
        var entity = CreateTestEntity();
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new TestShortcutHandler());
        var viewModel = new ViewEntityViewModel(
            entity,
            this.mainWindowViewModel,
            shortcutManager,
            indentLevel: 0);

        Assert.Empty(viewModel.Shortcuts);

        await viewModel.InitializeAsync();

        Assert.Contains(viewModel.Shortcuts, shortcut => shortcut.Shortcut == Shortcut.Open);
    }

    private ViewEntityViewModel CreateViewModel(bool isExpanded = true)
    {
        var entity = CreateTestEntity();
        var shortcutManager = new ShortcutManager();

        return new ViewEntityViewModel(
            entity,
            this.mainWindowViewModel,
            shortcutManager,
            indentLevel: 0,
            isExpanded: isExpanded);
    }

    private static SubscribedEntityViewModel CreateTestEntity()
    {
        var entityId = Guid.NewGuid();
        var snapshot = new EntitySnapshot
        {
            EntityId = new EntityId(entityId.ToString()),
            ConcurrencyTag = null,
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, Guid.NewGuid().ToString()),
            Data = JsonDocument.Parse("""{"display-name":"Test Entity","entity-types":["entity"]}""").RootElement.Clone(),
            Relationships = Array.Empty<EntitySnapshot>(),
        };

        return new SubscribedEntityViewModel(snapshot);
    }

    private sealed class TestShortcutHandler : ShortcutHandler
    {
        public override async ValueTask<bool> ShouldApplyTo(
            MainWindowViewModel mainWindowViewModel,
            Shortcut shortcut,
            SubscribedEntityViewModel entityViewModel)
        {
            await Task.Yield();
            return shortcut == Shortcut.Open;
        }

        public override Task<bool> Handle(
            MainWindowViewModel mainWindowViewModel,
            Shortcut shortcut,
            SubscribedEntityViewModel entityViewModel)
            => Task.FromResult(true);
    }
}
