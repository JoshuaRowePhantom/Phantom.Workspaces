using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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

    [AvaloniaFact]
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

        await viewModel.InitializeAsync();

        Assert.Contains(viewModel.Shortcuts, shortcut => shortcut.Shortcut == Shortcut.Open);
    }

    [AvaloniaFact]
    public async Task InitializeAsync_CompletesWithinTimeout_WhenDispatcherActive()
    {
        var entity = CreateTestEntity();
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new TestShortcutHandler());
        var viewModel = new ViewEntityViewModel(
            entity,
            this.mainWindowViewModel,
            shortcutManager,
            indentLevel: 0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var initTask = viewModel.InitializeAsync();
        var completed = await Task.WhenAny(initTask, Task.Delay(Timeout.Infinite, cts.Token));

        Assert.Same(initTask, completed);
        await initTask;
    }

    [AvaloniaFact]
    public async Task InitializeAsync_DoesNotPushShortcutsButTreeCardStillShowsThem()
    {
        var entity = CreateTestEntity();
        var shortcutManager = new ShortcutManager();
        shortcutManager.AddShortcutHandler(new TestShortcutHandler());
        var viewModel = new ViewEntityViewModel(
            entity,
            this.mainWindowViewModel,
            shortcutManager,
            indentLevel: 0);

        // The card resolves its own shortcuts (no SetShortcuts push from the view model).
        await viewModel.InitializeAsync();

        Assert.True(viewModel.EntityCardNode.Card.HasShortcuts);
        Assert.Contains(viewModel.EntityCardNode.Card.Shortcuts, shortcut => shortcut.Shortcut == Shortcut.Open);
        Assert.Same(viewModel.EntityCardNode.Card.Shortcuts, viewModel.Shortcuts);
    }

    [AvaloniaFact]
    public async Task QueryPopulatedView_ReceivesRefreshedResults_WhenRelationshipUpdatedWithoutRenavigation()
    {
        var ct = TestContext.Current.CancellationToken;
        var broker = await EntityBroker.CreateInitializedAsync(new UnknownRepositorySource(), ct);
        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;

        var visibleId = new EntityId(Guid.NewGuid());
        await SeedTaskAsync(dataAccessLayer, visibleId);

        // A query-populated view whose membership excludes not-interesting tasks: membership therefore
        // depends on a relationship, so toggling that relationship changes the query's results.
        var tasksQuery = new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("tasks"),
                    Clause = new EntityTypeQueryClause
                    {
                        EntityTypeNames = new EntityTypeNameSet(["task"]),
                    },
                },
            ],
        };

        var subscribedQuery = await broker.SubscribeQueryAsync(
            NotInterestingQuery.ExcludingNotInteresting(tasksQuery),
            ct);
        Assert.Contains(subscribedQuery.Results, entity => entity.EntityId == visibleId);

        // The populated view observes the live query results and rebinds in place (no dispose/recreate).
        var population = new ViewPopulationViewModel();
        var rebindCount = 0;
        population.AddQuerySubscription(subscribedQuery, () =>
        {
            rebindCount++;
            return Task.CompletedTask;
        });

        // Toggle 'not-interesting' on the task through the broker (a relationship update). This removes
        // the task from the query's membership, so the live results change with no re-navigation.
        var task = subscribedQuery.Results.Single(entity => entity.EntityId == visibleId);
        await task.ToggleInterestAsync("not-interesting");

        // Flush the rebind that was posted to the UI thread by the live results observer.
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(subscribedQuery.Results, entity => entity.EntityId == visibleId);
        Assert.True(rebindCount >= 1);

        await population.DisposeAsync();
    }

    private static async Task SeedTaskAsync(IDataAccessLayer dataAccessLayer, EntityId id)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{id.Value}}",
              "entity-types": ["entity", "task"],
              "names": [["tasks", "populated-view-task"]],
              "display-name": { "default": "Populated View Task" }
            }
            """);

        var result = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed task" } },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = id,
                        ConcurrencyTag = null,
                        Data = document.RootElement.Clone(),
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            },
            System.Threading.CancellationToken.None);

        var failure = result.EntityResults.FirstOrDefault(static entityResult => entityResult.UpdateState == UpdateState.Failed);
        Assert.True(failure is null, failure is null ? string.Empty : string.Join(" | ", failure.Errors.Select(static error => error.Message)));
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
