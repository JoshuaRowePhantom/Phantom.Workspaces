using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
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

    private static TreeView CreateTree(System.Collections.IEnumerable items)
    {
        var tree = new TreeView { ItemsSource = items };
        tree.Classes.Add("entity-card-tree");
        return tree;
    }

    private sealed class TreeNode(string name)
    {
        public string Name { get; } = name;
        public ObservableCollection<TreeNode> Children { get; } = [];
    }
}
