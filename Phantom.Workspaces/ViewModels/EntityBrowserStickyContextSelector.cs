using System;
using System.Collections.Generic;
using System.Linq;

namespace Phantom.Workspaces.ViewModels;

public readonly record struct VisibleEntityListItemPosition(
    string ItemKey,
    double Top,
    double Bottom);

public readonly record struct StickyFocusSelection(
    string? ItemKey,
    double ItemTop);

public readonly record struct StickyPinnedItemPosition(
    string ItemKey,
    double Top);

public readonly record struct StickyLayoutSelection(
    string? FocusedItemKey,
    IReadOnlyCollection<StickyPinnedItemPosition> PinnedItems);

public static class EntityBrowserStickyContextSelector
{
    public static StickyFocusSelection SelectFocusedItem(
        IReadOnlyCollection<VisibleEntityListItemPosition> visibleItems,
        double anchorY = 0)
    {
        var candidates = visibleItems
            .OrderBy(static item => item.Top)
            .ToArray();
        if (candidates.Length == 0)
        {
            return new StickyFocusSelection(null, 0);
        }

        var closestAtOrAboveAnchor = candidates
            .Where(item => item.Top <= anchorY)
            .OrderByDescending(static item => item.Top)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(closestAtOrAboveAnchor.ItemKey))
        {
            return new StickyFocusSelection(closestAtOrAboveAnchor.ItemKey, closestAtOrAboveAnchor.Top);
        }

        var firstBelowAnchor = candidates
            .Where(item => item.Top > anchorY)
            .OrderBy(static item => item.Top)
            .FirstOrDefault();
        return !string.IsNullOrEmpty(firstBelowAnchor.ItemKey)
            ? new StickyFocusSelection(firstBelowAnchor.ItemKey, firstBelowAnchor.Top)
            : new StickyFocusSelection(null, 0);
    }

    public static StickyLayoutSelection SelectPinnedItems(
        IReadOnlyCollection<VisibleEntityListItemPosition> visibleItems,
        IReadOnlyDictionary<string, string?> parentItemKeysByItemKey,
        IReadOnlyDictionary<string, double> heightsByItemKey)
    {
        var initialSelection = SelectFocusedItem(visibleItems);
        var initialAncestorKeys = GetAncestorKeys(initialSelection.ItemKey, parentItemKeysByItemKey);
        var initialAnchor = ComputeAnchorHeight(initialAncestorKeys, visibleItems, heightsByItemKey);

        var selection = SelectFocusedItem(visibleItems, initialAnchor);
        var ancestorKeys = GetAncestorKeys(selection.ItemKey, parentItemKeysByItemKey);
        var pinnedItems = new List<StickyPinnedItemPosition>();
        var stickyOffset = 0d;
        foreach (var ancestorKey in ancestorKeys)
        {
            if (!TryGetVisibleItem(ancestorKey, visibleItems, out var visibleItem)
                || !heightsByItemKey.TryGetValue(ancestorKey, out var height))
            {
                continue;
            }

            if (visibleItem.Top >= stickyOffset)
            {
                break;
            }

            pinnedItems.Add(new StickyPinnedItemPosition(ancestorKey, stickyOffset));
            stickyOffset += height;
        }

        return new StickyLayoutSelection(selection.ItemKey, pinnedItems);
    }

    private static IReadOnlyCollection<string> GetAncestorKeys(
        string? itemKey,
        IReadOnlyDictionary<string, string?> parentItemKeysByItemKey)
    {
        if (string.IsNullOrEmpty(itemKey))
        {
            return Array.Empty<string>();
        }

        var ancestors = new Stack<string>();
        var parentKey = parentItemKeysByItemKey.TryGetValue(itemKey, out var parent)
            ? parent
            : null;
        while (parentKey is not null && parentItemKeysByItemKey.ContainsKey(parentKey))
        {
            ancestors.Push(parentKey);
            parentKey = parentItemKeysByItemKey[parentKey];
        }

        return ancestors.ToArray();
    }

    private static double ComputeAnchorHeight(
        IReadOnlyCollection<string> ancestorKeys,
        IReadOnlyCollection<VisibleEntityListItemPosition> visibleItems,
        IReadOnlyDictionary<string, double> heightsByItemKey)
    {
        var stickyOffset = 0d;
        foreach (var ancestorKey in ancestorKeys)
        {
            if (!TryGetVisibleItem(ancestorKey, visibleItems, out var visibleItem)
                || !heightsByItemKey.TryGetValue(ancestorKey, out var height))
            {
                continue;
            }

            if (visibleItem.Top >= stickyOffset)
            {
                break;
            }

            stickyOffset += height;
        }

        return stickyOffset;
    }

    private static bool TryGetVisibleItem(
        string itemKey,
        IReadOnlyCollection<VisibleEntityListItemPosition> visibleItems,
        out VisibleEntityListItemPosition visibleItem)
    {
        foreach (var item in visibleItems)
        {
            if (string.Equals(item.ItemKey, itemKey, StringComparison.Ordinal))
            {
                visibleItem = item;
                return true;
            }
        }

        visibleItem = default;
        return false;
    }
}
