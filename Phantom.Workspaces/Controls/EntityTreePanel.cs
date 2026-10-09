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
        bool restored = false;
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

        bool FocusReplacement(TreeViewItem container)
        {
            InputElement? target = null;
            if (focusedDataContext is not null &&
                !ReferenceEquals(focusedDataContext, focusedItem))
            {
                target = container.GetVisualDescendants().OfType<Control>()
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
            target.Focus();
            return target.IsFocused;
        }

        tree.Classes.Set("entity-card-tree-small", small);
        if (scroll is null)
        {
            return;
        }

        void Restore(object? sender, EventArgs args)
        {
            if (restored || transition.Generation != generation)
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
            restored = true;
            foreach (var container in tree.GetVisualDescendants().OfType<TreeViewItem>())
            {
                if (expanded.Contains(container.DataContext))
                {
                    container.IsExpanded = true;
                }
            }

            // The desktop can complete a panel layout before a Loaded callback runs. Correct the
            // realized anchor and focus now so a live threshold change cannot visibly jump.
            bool focusRestored = false;
            if (focusedItem is not null && focused?.IsAttachedToVisualTree() != true)
            {
                var replacement = tree.GetVisualDescendants().OfType<TreeViewItem>()
                    .FirstOrDefault(item => ReferenceEquals(item.DataContext, focusedItem));
                if (replacement is not null)
                {
                    focusRestored = FocusReplacement(replacement);
                    tree.UpdateLayout();
                }
            }
            if (anchorItem is not null && anchorY is { } oldY)
            {
                var currentAnchor = tree.GetVisualDescendants().OfType<TreeViewItem>()
                    .FirstOrDefault(item => ReferenceEquals(item.DataContext, anchorItem));
                if (currentAnchor?.TranslatePoint(default, scroll)?.Y is { } currentY)
                {
                    scroll.Offset = new Vector(scroll.Offset.X,
                        Math.Max(0, scroll.Offset.Y + currentY - oldY));
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
                if (!focusRestored && focusedItem is not null && focused?.IsAttachedToVisualTree() != true)
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

                    if (container is not null)
                    {
                        FocusReplacement(container);
                    }
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
        // A panel replacement may finish layout synchronously before the handler is attached.
        Dispatcher.UIThread.Post(() => Restore(tree, EventArgs.Empty), DispatcherPriority.Loaded);
    }
}
