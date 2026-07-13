using System;
using System.Text.Json;
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
    public void HasTraversedChildren_CanBeSetToTrue()
    {
        var viewModel = this.CreateViewModel();

        viewModel.HasTraversedChildren = true;

        Assert.True(viewModel.HasTraversedChildren);
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
}
