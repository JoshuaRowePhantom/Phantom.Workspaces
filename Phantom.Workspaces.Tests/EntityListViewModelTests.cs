using Avalonia.Headless.XUnit;
using Avalonia;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class EntityListViewModelTests
{
    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
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

    [AvaloniaFact]
    public void SetItems_PreservesIsExpandedState_WhenKeyUnchanged()
    {
        var list = new EntityListViewModel();
        var node = new EntityListNodeViewModel(
            displayName: "Folder",
            entityType: "folder",
            nameComponents: ["folder"],
            sortKey: "[\"folder\"]");
        var child = new EntityListNodeViewModel(
            displayName: "Child",
            entityType: "entity",
            nameComponents: ["folder", "child"],
            sortKey: "[\"folder\",\"child\"]");
        node.SetChildren([child]);

        list.SetItems(
        [
            new EntityListItemViewModel(node, order: 1, level: 0, itemKey: "[\"folder\"]", childItemKeys: ["[\"folder\",\"child\"]"]),
        ]);

        var originalInstance = list.Items[0];
        originalInstance.IsExpanded = true;

        var node2 = new EntityListNodeViewModel(
            displayName: "Folder",
            entityType: "folder",
            nameComponents: ["folder"],
            sortKey: "[\"folder\"]");

        list.SetItems(
        [
            new EntityListItemViewModel(node2, order: 2, level: 0, itemKey: "[\"folder\"]"),
        ]);

        Assert.Single(list.Items);
        Assert.Same(originalInstance, list.Items[0]);
        Assert.True(list.Items[0].IsExpanded);
    }

    [AvaloniaFact]
    public void SetItems_AddsNewItem_WithoutAffectingExisting()
    {
        var list = new EntityListViewModel();
        var nodeA = new EntityListNodeViewModel(
            displayName: "A",
            entityType: "entity",
            nameComponents: ["a"],
            sortKey: "[\"a\"]");

        list.SetItems(
        [
            new EntityListItemViewModel(nodeA, order: 1, level: 0, itemKey: "[\"a\"]"),
        ]);

        var instanceA = list.Items[0];

        var nodeB = new EntityListNodeViewModel(
            displayName: "B",
            entityType: "entity",
            nameComponents: ["b"],
            sortKey: "[\"b\"]");
        var newNodeA = new EntityListNodeViewModel(
            displayName: "A",
            entityType: "entity",
            nameComponents: ["a"],
            sortKey: "[\"a\"]");

        list.SetItems(
        [
            new EntityListItemViewModel(newNodeA, order: 1, level: 0, itemKey: "[\"a\"]"),
            new EntityListItemViewModel(nodeB, order: 2, level: 0, itemKey: "[\"b\"]"),
        ]);

        Assert.Equal(2, list.Items.Count);
        Assert.Same(instanceA, list.Items[0]);
        Assert.Equal("[\"b\"]", list.Items[1].ItemKey);
    }

    [AvaloniaFact]
    public void SetItems_RemovesItem_LeavingOthersUntouched()
    {
        var list = new EntityListViewModel();
        var nodeA = new EntityListNodeViewModel(
            displayName: "A",
            entityType: "entity",
            nameComponents: ["a"],
            sortKey: "[\"a\"]");
        var nodeB = new EntityListNodeViewModel(
            displayName: "B",
            entityType: "entity",
            nameComponents: ["b"],
            sortKey: "[\"b\"]");

        list.SetItems(
        [
            new EntityListItemViewModel(nodeA, order: 1, level: 0, itemKey: "[\"a\"]"),
            new EntityListItemViewModel(nodeB, order: 2, level: 0, itemKey: "[\"b\"]"),
        ]);

        var instanceA = list.Items[0];

        var newNodeA = new EntityListNodeViewModel(
            displayName: "A",
            entityType: "entity",
            nameComponents: ["a"],
            sortKey: "[\"a\"]");

        list.SetItems(
        [
            new EntityListItemViewModel(newNodeA, order: 1, level: 0, itemKey: "[\"a\"]"),
        ]);

        Assert.Single(list.Items);
        Assert.Same(instanceA, list.Items[0]);
    }

    [AvaloniaFact]
    public void SetItems_MovesItemToCorrectPosition_WhenOrderChanges()
    {
        var list = new EntityListViewModel();
        var nodeA = new EntityListNodeViewModel(
            displayName: "A",
            entityType: "entity",
            nameComponents: ["a"],
            sortKey: "[\"a\"]");
        var nodeB = new EntityListNodeViewModel(
            displayName: "B",
            entityType: "entity",
            nameComponents: ["b"],
            sortKey: "[\"b\"]");

        list.SetItems(
        [
            new EntityListItemViewModel(nodeA, order: 1, level: 0, itemKey: "[\"a\"]"),
            new EntityListItemViewModel(nodeB, order: 2, level: 0, itemKey: "[\"b\"]"),
        ]);

        var instanceA = list.Items[0];
        var instanceB = list.Items[1];

        var newNodeA = new EntityListNodeViewModel(
            displayName: "A",
            entityType: "entity",
            nameComponents: ["a"],
            sortKey: "[\"a\"]");
        var newNodeB = new EntityListNodeViewModel(
            displayName: "B",
            entityType: "entity",
            nameComponents: ["b"],
            sortKey: "[\"b\"]");

        list.SetItems(
        [
            new EntityListItemViewModel(newNodeB, order: 1, level: 0, itemKey: "[\"b\"]"),
            new EntityListItemViewModel(newNodeA, order: 2, level: 0, itemKey: "[\"a\"]"),
        ]);

        Assert.Equal(2, list.Items.Count);
        Assert.Same(instanceB, list.Items[0]);
        Assert.Same(instanceA, list.Items[1]);
    }

    // Issue #1177: rebuilding the browser tree with many entities must not eagerly build field
    // editors — cards are constructed with empty field-editor collections and rely on
    // EntityCardControl.OnAttachedToVisualTree to lazily invoke EnsureFieldEditorsBuilt.
    [AvaloniaFact(Timeout = 30_000)]
    public async Task EntityBrowserWorkspaceTabViewModel_BuildChildren_DoesNotBuildFieldEditorsForNonRealizedEntities()
    {
        var broker = await Phantom.Workspaces.EntityBroker.CreateInitializedAsync(
            new Phantom.Workspaces.UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        for (var i = 1; i <= 4; i++)
        {
            var id = new EntityId($"{i:D8}-{i:D4}-{i:D4}-{i:D4}-{i:D12}");
            var json = $$"""
                {
                  "entity-id": "{{id}}",
                  "entity-types": ["entity", "folder"],
                  "names": [["folder-{{i}}"]],
                  "display-name": { "default": "Folder {{i}}" }
                }
                """;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var modified = new Timestamp(DateTimeOffset.UtcNow.AddMinutes(-i), i.ToString());
            var snapshot = new EntitySnapshot
            {
                EntityId = id,
                ConcurrencyTag = new ConcurrencyTag(modified.ChangeId),
                ModifiedTime = modified,
                Data = doc.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            };
            await broker.EntityRepository.DataAccessLayer.UpdateAsync(
                new UpdateRequest
                {
                    UpdateMetadata = new UpdateMetadata
                    {
                        Comment = new Markdown { Text = "seed" },
                    },
                    Changes =
                    [
                        new EntityChange
                        {
                            EntityId = id,
                            EntityChangeMode = EntityChangeMode.Replace,
                            Data = snapshot.Data?.Clone(),
                        },
                    ],
                },
                TestContext.Current.CancellationToken);
        }

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
            Id = "entity-browser-non-realized",
            Title = "Non-realized",
        };
        try
        {
            var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs __)
            {
                if (viewModel.EntityList.Items.Any(item =>
                        item.ItemKey.StartsWith("[\"folder-", StringComparison.Ordinal)))
                {
                    signal.TrySetResult();
                }
            }

            viewModel.EntityList.Items.CollectionChanged += OnChanged;
            try
            {
                if (!viewModel.EntityList.Items.Any(item =>
                        item.ItemKey.StartsWith("[\"folder-", StringComparison.Ordinal)))
                {
                    await signal.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                }
            }
            finally
            {
                viewModel.EntityList.Items.CollectionChanged -= OnChanged;
            }

            var builtCount = viewModel.EntityList.Items.Count(item => item.Node.Card.FieldEditors.Count > 0);
            Assert.Equal(0, builtCount);
        }
        finally
        {
            await viewModel.DisposeAsync();
        }
    }

    // Issue #1177: binding an entity-card TreeView to a thousand items in a bounded viewport
    // realizes only a small, viewport-proportional number of TreeViewItem containers.
    [AvaloniaFact(Timeout = 30_000)]
    public void EntityCardTreeView_WhenBoundToThousandsOfItems_RealizesBoundedNumberOfContainers()
    {
        var items = new string[1000];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = $"item-{i:D4}";
        }

        var tree = new Avalonia.Controls.TreeView();
        tree.Classes.Add("entity-card-tree");
        tree.ItemsSource = items;
        var window = new Avalonia.Controls.Window { Content = tree, Width = 400, Height = 400 };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

            var panel = tree.ItemsPanelRoot;
            Assert.NotNull(panel);
            Assert.IsType<Avalonia.Controls.VirtualizingStackPanel>(panel);

            var realizedCount = panel!.Children.Count;
            Assert.True(realizedCount > 0, "At least one item must be realized.");
            Assert.True(realizedCount < items.Length / 2, $"Realized container count was {realizedCount} of {items.Length}; expected a viewport-bounded fraction.");
        }
        finally
        {
            window.Close();
        }
    }
}
