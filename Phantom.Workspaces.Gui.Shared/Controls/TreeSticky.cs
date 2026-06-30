using System;
using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Phantom.Workspaces.Gui.Shared.Controls;

public static class TreeSticky
{
    private sealed class Owner { }

    public static readonly AttachedProperty<bool> AutoRowLevelProperty =
        AvaloniaProperty.RegisterAttached<Owner, Control, bool>("AutoRowLevel");

    static TreeSticky()
    {
        AutoRowLevelProperty.Changed.AddClassHandler<Control>(static (control, e) =>
        {
            if (e.NewValue is true)
            {
                control.AttachedToVisualTree += OnAttachedToVisualTree;
                control.LayoutUpdated += OnLayoutUpdated;
                UpdateStickyRowLevel(control);
            }
            else
            {
                control.AttachedToVisualTree -= OnAttachedToVisualTree;
                control.LayoutUpdated -= OnLayoutUpdated;
                StickyItem.SetRow(control, null);
            }
        });
    }

    public static bool GetAutoRowLevel(Control element) => element.GetValue(AutoRowLevelProperty);
    public static void SetAutoRowLevel(Control element, bool value) => element.SetValue(AutoRowLevelProperty, value);

    private static void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control)
        {
            UpdateStickyRowLevel(control);
        }
    }

    private static void OnLayoutUpdated(object? sender, System.EventArgs e)
    {
        if (sender is Control control)
        {
            UpdateStickyRowLevel(control);
        }
    }

    private static void UpdateStickyRowLevel(Control control)
    {
        if (!IsExpandableTreeNode(control))
        {
            StickyItem.SetRow(control, null);
            return;
        }

        var level = 0;
        var current = control.GetVisualParent();
        while (current is not null)
        {
            if (current is TreeViewItem)
            {
                level++;
            }

            current = current.GetVisualParent();
        }

        StickyItem.SetRow(control, level > 0 ? level - 1 : 0);
    }

    private static bool IsExpandableTreeNode(Control control)
    {
        var current = control.GetVisualParent();
        while (current is not null && current is not TreeViewItem)
        {
            current = current.GetVisualParent();
        }

        if (current is not TreeViewItem treeItem)
        {
            return false;
        }

        var dataContext = treeItem.DataContext;
        return IsExpandableDataContext(dataContext);
    }

    internal static bool IsExpandableDataContext(object? dataContext)
    {
        if (dataContext is null)
        {
            return false;
        }

        var hasChildrenProperty = dataContext.GetType().GetProperty("HasChildren");
        if (hasChildrenProperty?.PropertyType == typeof(bool))
        {
            return (bool?)hasChildrenProperty.GetValue(dataContext) ?? false;
        }

        var notHasChildrenProperty = dataContext.GetType().GetProperty("NotHasChildren");
        if (notHasChildrenProperty?.PropertyType == typeof(bool))
        {
            return !((bool?)notHasChildrenProperty.GetValue(dataContext) ?? true);
        }

        var childrenProperty = dataContext.GetType().GetProperty("Children");
        if (HasAny(childrenProperty?.GetValue(dataContext)))
        {
            return true;
        }

        var visibleChildrenProperty = dataContext.GetType().GetProperty("VisibleChildren");
        if (HasAny(visibleChildrenProperty?.GetValue(dataContext)))
        {
            return true;
        }

        return false;
    }

    internal static bool HasAny(object? value)
    {
        if (value is null)
        {
            return false;
        }

        if (value is ICollection collection)
        {
            return collection.Count > 0;
        }

        if (value is IEnumerable enumerable)
        {
            var enumerator = enumerable.GetEnumerator();
            try
            {
                return enumerator.MoveNext();
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }

        return false;
    }
}
