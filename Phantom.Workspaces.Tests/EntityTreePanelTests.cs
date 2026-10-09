using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Avalonia.Input;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Controls;
using Phantom.Workspaces.Testing.Gui;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class EntityTreePanelTests
{
    [AvaloniaTheory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(1000, true)]
    public void EntityCardTreeView_WhenBoundToPopulation_UsesPanelForCompleteNodeCount(int count, bool virtualized)
    {
        var list = new EntityListViewModel();
        for (int i = 0; i < count; i++)
        {
            var node = new EntityListNodeViewModel($"Node {i}", "entity", [$"node-{i}"], $"node-{i}");
            list.Items.Add(new EntityListItemViewModel(node, i, 0, $"node-{i}"));
        }

        var tree = CreateTree(list.Items);
        tree.Bind(EntityTreePanel.NodeCountProperty, new Binding("Items.Count") { Source = list });
        var window = new Window { Content = tree, Width = 400, Height = 320 };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(virtualized, tree.ItemsPanelRoot is VirtualizingStackPanel);
            if (!virtualized)
            {
                Assert.IsType<StackPanel>(tree.ItemsPanelRoot);
            }
            else if (count == 1000)
            {
                Assert.True(tree.ItemsPanelRoot!.Children.Count < count / 2);
            }
            Assert.Single(tree.GetVisualDescendants().OfType<ScrollViewer>());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EntityCardTreeView_WhenNestedViewHasHundredNodes_UsesVirtualizingPanel()
    {
        var roots = new ObservableCollection<TreeNode>([new TreeNode("First"), new TreeNode("Second")]);
        var nodes = new ObservableCollection<TreeNode>(roots);
        for (int i = 0; i < 98; i++)
        {
            var child = new TreeNode($"Child {i}");
            roots[0].Children.Add(child);
            nodes.Add(child);
        }
        var tree = CreateTree(roots);
        tree.ItemTemplate = new Avalonia.Controls.Templates.FuncTreeDataTemplate<TreeNode>(
            (node, _) => new TextBlock { Text = node.Name, Height = 34 },
            node => node.Children);
        tree.Bind(EntityTreePanel.NodeCountProperty, new Binding("Count") { Source = nodes });
        var window = new Window { Content = tree, Width = 400, Height = 320 };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, roots.Count);
            Assert.IsType<VirtualizingStackPanel>(tree.ItemsPanelRoot);
            Assert.False(Assert.IsType<TreeViewItem>(tree.ContainerFromIndex(0)).IsExpanded);
            Assert.Equal(100, nodes.Count);
            roots[0].Children.RemoveAt(97);
            nodes.RemoveAt(99);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsType<StackPanel>(tree.ItemsPanelRoot);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(99)]
    [InlineData(100)]
    public void EntityCardTreeView_BothPanelsRetainNarrowViewportHorizontalScroll(int count)
    {
        var nodes = new ObservableCollection<string>(Enumerable.Range(0, count).Select(i => $"Node {i}"));
        var tree = CreateTree(nodes);
        tree.Bind(EntityTreePanel.NodeCountProperty, new Binding("Count") { Source = nodes });
        var window = new Window { Content = tree, Width = 120, Height = 320 };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var panel = Assert.IsAssignableFrom<Panel>(tree.ItemsPanelRoot);
            var scroll = Assert.Single(tree.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.Equal(160, panel.MinWidth);
            Assert.True(scroll.Extent.Width >= 160);
            Assert.True(scroll.Viewport.Width < 160);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("entity-card-tree")]
    [InlineData("entity-card-tree-view")]
    public void OtherSharedTreeConsumers_WithoutNodeCount_KeepVirtualization(string styleClass)
    {
        var tree = new TreeView { ItemsSource = new[] { "item" } };
        tree.Classes.Add(styleClass);
        var window = new Window { Content = tree, Width = 400, Height = 320 };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsType<VirtualizingStackPanel>(tree.ItemsPanelRoot);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EntityCardTreeView_WhenPopulationCrossesThreshold_PreservesSelectionExpansionFocusAndScroll()
    {
        var nodes = new ObservableCollection<TreeNode>(
            Enumerable.Range(0, 99).Select(i => new TreeNode($"Node {i}")));
        nodes[0].Children.Add(new TreeNode("Child"));
        var tree = CreateTree(nodes);
        tree.ItemTemplate = new Avalonia.Controls.Templates.FuncTreeDataTemplate<TreeNode>(
            (node, _) => new TextBlock { Text = node.Name, Height = 34 },
            node => node.Children);
        tree.Bind(EntityTreePanel.NodeCountProperty, new Binding("Count") { Source = nodes });
        var window = new Window { Content = tree, Width = 400, Height = 320 };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var first = Assert.IsType<TreeViewItem>(tree.ContainerFromIndex(0));
            first.IsExpanded = true;
            tree.SelectedItem = nodes[0];
            Assert.IsType<TreeViewItem>(tree.ContainerFromIndex(3)).Focus();
            var scroll = Assert.Single(tree.GetVisualDescendants().OfType<ScrollViewer>());
            scroll.Offset = new Vector(0, 48);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var offset = scroll.Offset.Y;
            nodes.Add(new TreeNode("Node 99"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsType<VirtualizingStackPanel>(tree.ItemsPanelRoot);
            Assert.Same(nodes[0], tree.SelectedItem);
            Assert.True(Assert.IsType<TreeViewItem>(tree.ContainerFromIndex(0)).IsExpanded);
            Assert.Equal(offset, scroll.Offset.Y, 1);
            Assert.True(Assert.IsType<TreeViewItem>(tree.ContainerFromIndex(3)).IsFocused);

            nodes.RemoveAt(99);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsType<StackPanel>(tree.ItemsPanelRoot);
            Assert.Same(nodes[0], tree.SelectedItem);
            Assert.True(Assert.IsType<TreeViewItem>(tree.ContainerFromIndex(0)).IsExpanded);
            Assert.Equal(offset, scroll.Offset.Y, 1);
            Assert.True(Assert.IsType<TreeViewItem>(tree.ContainerFromIndex(3)).IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EntityCardTreeView_InsertAndRemoveBeforeViewport_PreserveVisibleObjectAtVariableHeights()
    {
        var nodes = new ObservableCollection<TreeNode>(
            Enumerable.Range(0, 99).Select(i => new TreeNode($"Node {i}")));
        var tree = CreateTree(nodes);
        tree.ItemTemplate = new Avalonia.Controls.Templates.FuncTreeDataTemplate<TreeNode>(
            (node, _) => new TextBlock { Text = node.Name, Height = node.Height },
            node => node.Children);
        tree.Bind(EntityTreePanel.NodeCountProperty, new Binding("Count") { Source = nodes });
        var window = new Window { Content = tree, Width = 400, Height = 240 };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var scroll = Assert.Single(tree.GetVisualDescendants().OfType<ScrollViewer>());
            scroll.Offset = new Vector(0, 410);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var anchor = Assert.IsType<TreeViewItem>(tree.ContainerFromItem(nodes[7]));
            var before = Assert.NotNull(anchor.TranslatePoint(default, scroll)).Y;

            var inserted = new TreeNode("Tall item") { Height = 91 };
            nodes.Insert(3, inserted);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsType<VirtualizingStackPanel>(tree.ItemsPanelRoot);
            Assert.Equal(before, Assert.NotNull(
                Assert.IsType<TreeViewItem>(tree.ContainerFromItem(anchor.DataContext!))
                    .TranslatePoint(default, scroll)).Y, 1);

            nodes.Remove(inserted);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsType<StackPanel>(tree.ItemsPanelRoot);
            Assert.Equal(before, Assert.NotNull(
                Assert.IsType<TreeViewItem>(tree.ContainerFromItem(anchor.DataContext!))
                    .TranslatePoint(default, scroll)).Y, 1);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EntityCardTreeView_ThresholdChangesBeforeLoaded_PreserveVisibleAnchorAndEditorFocus()
    {
        var nodes = new ObservableCollection<TreeNode>(
            Enumerable.Range(0, 99).Select(i => new TreeNode($"Node {i}")));
        var inserted = new TreeNode("Tall item") { Height = 91 };
        nodes.Insert(3, inserted);
        var tree = CreateTree(nodes);
        tree.ItemTemplate = new Avalonia.Controls.Templates.FuncTreeDataTemplate<TreeNode>(
            (node, _) => new TextBox { Text = node.Name, Height = node.Height },
            node => node.Children);
        tree.Bind(EntityTreePanel.NodeCountProperty, new Binding("Count") { Source = nodes });
        var window = new Window { Content = tree, Width = 400, Height = 240 };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var scroll = tree.GetVisualDescendants().OfType<ScrollViewer>().First();
            var focusedNode = nodes[30];

            void AssertImmediateTransition(Action change, Type expectedPanel)
            {
                var original = Assert.IsType<TreeViewItem>(tree.ContainerFromItem(focusedNode));
                var editor = Assert.Single(original.GetVisualDescendants().OfType<TextBox>());
                Assert.True(editor.Focus());
                var screenY = Assert.NotNull(original.TranslatePoint(default, window)).Y;
                Assert.InRange(screenY, 0, window.Bounds.Height);
                bool loadedCallbackRan = false;
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => loadedCallbackRan = true, Avalonia.Threading.DispatcherPriority.Loaded);

                change();
                // Offset invalidation from the panel swap can require a second synchronous
                // layout pass; neither pass may rely on the queued Loaded restoration.
                tree.UpdateLayout();
                tree.UpdateLayout();
                Assert.False(loadedCallbackRan);
                Assert.IsType(expectedPanel, tree.ItemsPanelRoot);
                var replacement = Assert.IsType<TreeViewItem>(tree.ContainerFromItem(focusedNode));
                Assert.Equal(screenY, Assert.NotNull(replacement.TranslatePoint(default, window)).Y, 1);
                Assert.True(Assert.Single(replacement.GetVisualDescendants().OfType<TextBox>()).IsFocused);
            }

            tree.ScrollIntoView(focusedNode);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var initialY = Assert.NotNull(
                Assert.IsType<TreeViewItem>(tree.ContainerFromItem(focusedNode))
                    .TranslatePoint(default, window)).Y;
            scroll.Offset = new Vector(0, scroll.Offset.Y + initialY - 40);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AssertImmediateTransition(() => nodes.Remove(inserted), typeof(StackPanel));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AssertImmediateTransition(() => nodes.Insert(3, inserted), typeof(VirtualizingStackPanel));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EntityCardTreeView_NestedEditorFocus_SurvivesBothPanelTransitions()
    {
        var nodes = new ObservableCollection<TreeNode>(
            Enumerable.Range(0, 99).Select(i => new TreeNode($"Node {i}")));
        var child = new TreeNode("Nested");
        nodes[0].Children.Add(child);
        var tree = CreateTree(nodes);
        tree.ItemTemplate = new Avalonia.Controls.Templates.FuncTreeDataTemplate<TreeNode>(
            (node, _) => new Button { Name = "Editor", Content = node.Name, Height = 34 },
            node => node.Children);
        tree.Bind(EntityTreePanel.NodeCountProperty, new Binding("Count") { Source = nodes });
        var window = new Window { Content = tree, Width = 400, Height = 240 };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsType<TreeViewItem>(tree.ContainerFromIndex(0)).IsExpanded = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var editor = tree.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Nested"));
            editor.Focus();
            Assert.True(editor.IsFocused);

            nodes.Add(new TreeNode("Node 99"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(tree.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Nested")).IsFocused);
            nodes.RemoveAt(99);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(tree.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Nested")).IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EntityCardTreeView_RapidThresholdReversals_DoNotRestoreStaleAnchorOrFocus()
    {
        var nodes = new ObservableCollection<TreeNode>(
            Enumerable.Range(0, 99).Select(i => new TreeNode($"Node {i}")));
        var tree = CreateTree(nodes);
        tree.ItemTemplate = new Avalonia.Controls.Templates.FuncTreeDataTemplate<TreeNode>(
            (node, _) => new Button { Content = node.Name, Height = 34 },
            node => node.Children);
        tree.Bind(EntityTreePanel.NodeCountProperty, new Binding("Count") { Source = nodes });
        var window = new Window { Content = tree, Width = 400, Height = 240 };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var scroll = Assert.Single(tree.GetVisualDescendants().OfType<ScrollViewer>());
            scroll.Offset = new Vector(0, 500);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var original = new TreeNode("First insertion") { Height = 91 };
            nodes.Insert(2, original);
            nodes.Remove(original);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsType<StackPanel>(tree.ItemsPanelRoot);

            scroll.Offset = new Vector(0, 1200);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var focused = tree.GetVisualDescendants().OfType<Button>()
                .First(button => Equals(button.Content, "Node 20"));
            focused.Focus();
            var before = Assert.NotNull(
                Assert.IsType<TreeViewItem>(tree.ContainerFromItem(nodes[20]))
                    .TranslatePoint(default, scroll)).Y;
            nodes.Insert(2, new TreeNode("Second insertion") { Height = 91 });
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsType<VirtualizingStackPanel>(tree.ItemsPanelRoot);
            Assert.Equal(before, Assert.NotNull(
                Assert.IsType<TreeViewItem>(tree.ContainerFromItem(nodes[21]))
                    .TranslatePoint(default, scroll)).Y, 1);
            Assert.True(tree.GetVisualDescendants().OfType<Button>()
                .Single(button => Equals(button.Content, "Node 20")).IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EntityCardTreeView_UnrealizedFocusedItemWithVisibleAnchor_RestoresFocusedControl()
    {
        var nodes = new ObservableCollection<TreeNode>(
            Enumerable.Range(0, 99).Select(i => new TreeNode($"Node {i}")));
        var tree = CreateTree(nodes);
        tree.ItemTemplate = new Avalonia.Controls.Templates.FuncTreeDataTemplate<TreeNode>(
            (node, _) => new TextBox { Text = node.Name, Height = 34 },
            node => node.Children);
        tree.Bind(EntityTreePanel.NodeCountProperty, new Binding("Count") { Source = nodes });
        var window = new Window { Content = tree, Width = 400, Height = 240 };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var focused = tree.GetVisualDescendants().OfType<TextBox>()
                .Single(box => box.Text == "Node 80");
            focused.Focus();
            Assert.True(focused.IsFocused);
            var scroll = tree.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.Offset = default;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(focused.IsFocused);

            nodes.Add(new TreeNode("Node 99"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.IsType<VirtualizingStackPanel>(tree.ItemsPanelRoot);
            Assert.Contains(tree.GetVisualDescendants().OfType<TextBox>(),
                box => box.Text == "Node 80" && box.IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EntityCardTreeView_RealCardFieldEditor_RetainsSemanticTextBoxFocusAcrossThreshold()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""{"display-name":"Card","entity-types":["entity"]}""");
        var entity = new SubscribedEntityViewModel(new EntitySnapshot
        {
            EntityId = new EntityId(Guid.NewGuid().ToString()),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, "1"),
            Data = doc.RootElement.Clone(),
            Relationships = [],
        });
        var field = new StringFieldEditorViewModel("path", "editable value");
        var card = new EntityCardViewModel(entity, [field]);
        card.EnterEditMode();
        var nodes = new ObservableCollection<CardNode>(
            Enumerable.Range(0, 99).Select(i => new CardNode($"Node {i}", i == 0 ? card : null)));
        var tree = CreateTree(nodes);
        tree.ItemTemplate = new Avalonia.Controls.Templates.FuncTreeDataTemplate<CardNode>(
            (node, _) => node.Card is null
                ? new TextBlock { Text = node.Name, Height = 34 }
                : new EntityCardControl { DataContext = node.Card },
            node => node.Children);
        tree.Bind(EntityTreePanel.NodeCountProperty, new Binding("Count") { Source = nodes });
        var window = new Window { Content = tree, Width = 400, Height = 320 };
        try
        {
            window.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var editor = tree.GetVisualDescendants().OfType<TextBox>()
                .Single(box => ReferenceEquals(box.DataContext, field));
            Assert.True(editor.Focus());
            Assert.True(editor.IsFocused);
            nodes.Add(new CardNode("Node 99", null));
            card.SetFieldEditors([new StringFieldEditorViewModel("new field", "other"), field]);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(tree.GetVisualDescendants().OfType<TextBox>()
                .Single(box => ReferenceEquals(box.DataContext, field)).IsFocused);
            nodes.RemoveAt(99);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(tree.GetVisualDescendants().OfType<TextBox>()
                .Single(box => ReferenceEquals(box.DataContext, field)).IsFocused);
        }
        finally
        {
            window.Close();
        }
    }

    private static TreeView CreateTree(System.Collections.IEnumerable items)
    {
        var tree = new TreeView { ItemsSource = items };
        tree.Classes.Add("entity-card-tree");
        return tree;
    }

    private sealed class TreeNode(string name)
    {
        public string Name { get; } = name;
        public double Height { get; init; } = 34;
        public ObservableCollection<TreeNode> Children { get; } = [];
    }

    private sealed class CardNode(string name, EntityCardViewModel? card)
    {
        public string Name { get; } = name;
        public EntityCardViewModel? Card { get; } = card;
        public ObservableCollection<CardNode> Children { get; } = [];
    }
}
