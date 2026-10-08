using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.Controls;

/// <summary>Opt-in panel selection for entity trees, based on all object nodes, not just roots.</summary>
public static class EntityTreePanel
{
    private static readonly ConditionalWeakTable<TreeView, TransitionState> Transitions = new();

    private sealed class TransitionState
    {
        public int Generation;
        public Action? Cancel;
    }

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

        var transition = Transitions.GetOrCreateValue(tree);
        transition.Cancel?.Invoke();
        int generation = ++transition.Generation;
        var scroll = tree.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var offset = scroll?.Offset;
        var expanded = tree.GetVisualDescendants().OfType<TreeViewItem>()
            .Where(item => item.IsExpanded).Select(item => item.DataContext).ToHashSet();
        var anchor = scroll is null ? null : tree.GetVisualDescendants().OfType<TreeViewItem>()
            .Select(item => (Item: item, Position: item.TranslatePoint(default, scroll!)))
            .Where(entry => entry.Position is { } position &&
                position.Y <= 0 && position.Y + entry.Item.Bounds.Height > 0)
            .OrderByDescending(entry => entry.Position!.Value.Y)
            .FirstOrDefault().Item;
        var anchorItem = anchor?.DataContext;
        var anchorY = anchor?.TranslatePoint(default, scroll!)?.Y;
        var focused = tree.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control.IsFocused);
        var focusedContainer = focused as TreeViewItem ??
            focused?.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
        var focusedItem = focusedContainer?.DataContext;
        var focusedDataContext = focused?.DataContext;
        var focusPath = new List<int>();
        for (Visual? current = focused; current is not null && current != focusedContainer;
             current = current.GetVisualParent())
        {
            if (current.GetVisualParent() is { } parent)
            {
                focusPath.Add(parent.GetVisualChildren().ToList().IndexOf(current));
            }
        }
        focusPath.Reverse();

        tree.Classes.Set("entity-card-tree-small", small);
        if (scroll is null)
        {
            return;
        }

        void Restore(object? sender, EventArgs args)
        {
            if (transition.Generation != generation)
            {
                Cancel();
                return;
            }

            if (tree.ItemsPanelRoot is null ||
                (tree.ItemsPanelRoot is VirtualizingStackPanel) == small)
            {
                return;
            }

            Cancel();
            foreach (var container in tree.GetVisualDescendants().OfType<TreeViewItem>())
            {
                if (expanded.Contains(container.DataContext))
                {
                    container.IsExpanded = true;
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (transition.Generation != generation || !tree.IsAttachedToVisualTree())
                {
                    return;
                }

                if (anchorItem is not null && tree.ContainerFromItem(anchorItem) is null)
                {
                    tree.ScrollIntoView(anchorItem);
                    tree.UpdateLayout();
                }

                bool focusRequiresScroll = false;
                if (focusedItem is not null && focused?.IsAttachedToVisualTree() != true)
                {
                    var container = tree.GetVisualDescendants().OfType<TreeViewItem>()
                        .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, focusedItem));
                    if (container is null)
                    {
                        tree.ScrollIntoView(focusedItem);
                        tree.UpdateLayout();
                        container = tree.GetVisualDescendants().OfType<TreeViewItem>()
                            .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, focusedItem));
                        focusRequiresScroll = container is not null;
                    }

                    InputElement? target = null;
                    if (focusedDataContext is not null &&
                        !ReferenceEquals(focusedDataContext, focusedItem))
                    {
                        target = container?.GetVisualDescendants().OfType<Control>()
                            .FirstOrDefault(control =>
                                control.GetType() == focused?.GetType() &&
                                ReferenceEquals(control.DataContext, focusedDataContext));
                    }
                    if (target is null)
                    {
                        Visual? replacement = container;
                        foreach (int index in focusPath)
                        {
                            var children = replacement?.GetVisualChildren().ToList();
                            replacement = children is not null && index >= 0 && index < children.Count
                                ? children[index] : null;
                        }
                        target = replacement as InputElement ?? container;
                    }
                    target?.Focus();
                }

                if (!focusRequiresScroll && offset is { } oldOffset)
                {
                    if (anchorItem is not null && anchorY is { } y)
                    {
                        var newAnchor = tree.GetVisualDescendants().OfType<TreeViewItem>()
                            .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, anchorItem));
                        var newY = newAnchor?.TranslatePoint(default, scroll)?.Y;
                        scroll.Offset = new Vector(oldOffset.X,
                            newY is { } actualY
                                ? Math.Max(0, scroll.Offset.Y + actualY - y)
                                : oldOffset.Y);
                    }
                    else
                    {
                        scroll.Offset = oldOffset;
                    }
                }
            }, DispatcherPriority.Loaded);
        }

        void Cancel()
        {
            tree.LayoutUpdated -= Restore;
            if (transition.Generation == generation)
            {
                transition.Cancel = null;
            }
        }

        transition.Cancel = Cancel;
        tree.LayoutUpdated += Restore;
    }
}
