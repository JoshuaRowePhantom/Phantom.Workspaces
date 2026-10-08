using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Phantom.Workspaces.Controls;

/// <summary>Opt-in panel selection for entity trees, based on all object nodes, not just roots.</summary>
public static class EntityTreePanel
{
    public static readonly AttachedProperty<int> NodeCountProperty =
        AvaloniaProperty.RegisterAttached<TreeView, int>("NodeCount", typeof(EntityTreePanel), -1);

    static EntityTreePanel()
    {
        NodeCountProperty.Changed.AddClassHandler<TreeView>((tree, _) => UpdatePanel(tree));
    }

    public static int GetNodeCount(TreeView tree) => tree.GetValue(NodeCountProperty);

    public static void SetNodeCount(TreeView tree, int count) => tree.SetValue(NodeCountProperty, count);

    private static void UpdatePanel(TreeView tree)
    {
        bool small = GetNodeCount(tree) < 100;
        if (tree.Classes.Contains("entity-card-tree-small") == small)
        {
            return;
        }

        var scroll = tree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var offset = scroll?.Offset;
        var expanded = tree.GetVisualDescendants().OfType<TreeViewItem>()
            .Where(item => item.IsExpanded).Select(item => item.DataContext).ToHashSet();
        var focused = tree.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control.IsFocused);
        var focusedItem = focused is TreeViewItem item ? item.DataContext :
            focused?.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault()?.DataContext;

        tree.Classes.Set("entity-card-tree-small", small);
        if (scroll is null)
        {
            return;
        }

        void Restore(object? sender, EventArgs args)
        {
            if (tree.ItemsPanelRoot is null ||
                (tree.ItemsPanelRoot is VirtualizingStackPanel) == small)
            {
                return;
            }

            tree.LayoutUpdated -= Restore;
            foreach (var container in tree.GetVisualDescendants().OfType<TreeViewItem>())
            {
                if (expanded.Contains(container.DataContext))
                {
                    container.IsExpanded = true;
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!tree.IsAttachedToVisualTree())
                {
                    return;
                }

                if (focusedItem is not null)
                {
                    var container = tree.GetVisualDescendants().OfType<TreeViewItem>()
                        .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, focusedItem));
                    if (focused?.IsAttachedToVisualTree() == true)
                    {
                        focused.Focus();
                    }
                    else
                    {
                        container?.Focus();
                    }
                }

                if (offset is { } oldOffset)
                {
                    scroll.Offset = oldOffset;
                }
            }, DispatcherPriority.Loaded);
        }

        tree.LayoutUpdated += Restore;
    }
}
