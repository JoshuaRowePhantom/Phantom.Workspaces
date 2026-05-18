using System.Collections.Generic;
using System.Linq;

namespace Phantom.Workspaces.ViewModels;

public readonly record struct VisibleEntityListItemPosition(
    string ItemKey,
    double Top,
    double Bottom);

public static class EntityBrowserStickyContextSelector
{
    public static string? SelectFocusedItemKey(
        IReadOnlyCollection<VisibleEntityListItemPosition> visibleItems)
    {
        var candidates = visibleItems
            .Where(static item => item.Bottom > 0)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var firstAtOrBelowViewportTop = candidates
            .Where(static item => item.Top >= 0)
            .OrderBy(static item => item.Top)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(firstAtOrBelowViewportTop.ItemKey))
        {
            return firstAtOrBelowViewportTop.ItemKey;
        }

        return candidates
            .OrderByDescending(static item => item.Top)
            .First()
            .ItemKey;
    }
}
