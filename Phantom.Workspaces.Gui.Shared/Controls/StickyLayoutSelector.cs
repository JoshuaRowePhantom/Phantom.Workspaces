using System.Collections.Generic;
using System.Linq;

namespace Phantom.Workspaces.Gui.Shared.Controls;

public readonly record struct StickyItemMeasurement(
    object Key,
    double Top,
    double Left,
    double Height,
    double Width,
    int? VerticalLevel,
    int? HorizontalLevel);

public readonly record struct StickyPinTarget(
    object Key,
    double? PinY,
    double? PinX);

public static class StickyLayoutSelector
{
    public static IReadOnlyList<StickyPinTarget> ComputePins(
        IReadOnlyCollection<StickyItemMeasurement> items)
    {
        var verticalPins = ComputeAxisPins(
            items
                .Where(static i => i.VerticalLevel.HasValue)
                .Select(static i => new AxisItem(i.Key, i.VerticalLevel!.Value, i.Top, i.Height))
                .ToList());

        var horizontalPins = ComputeAxisPins(
            items
                .Where(static i => i.HorizontalLevel.HasValue)
                .Select(static i => new AxisItem(i.Key, i.HorizontalLevel!.Value, i.Left, i.Width))
                .ToList());

        var allKeys = new HashSet<object>();
        foreach (var key in verticalPins.Keys)
        {
            allKeys.Add(key);
        }

        foreach (var key in horizontalPins.Keys)
        {
            allKeys.Add(key);
        }

        var result = new List<StickyPinTarget>(allKeys.Count);
        foreach (var key in allKeys)
        {
            result.Add(new StickyPinTarget(
                key,
                verticalPins.TryGetValue(key, out var py) ? py : null,
                horizontalPins.TryGetValue(key, out var px) ? px : null));
        }

        return result;
    }

    private readonly record struct AxisItem(object Key, int Level, double Position, double Size);

    private static Dictionary<object, double> ComputeAxisPins(IReadOnlyList<AxisItem> items)
    {
        var result = new Dictionary<object, double>();
        if (items.Count == 0)
        {
            return result;
        }

        var accumulated = 0.0;
        var levels = items
            .Select(static i => i.Level)
            .Distinct()
            .OrderBy(static l => l)
            .ToList();

        foreach (var level in levels)
        {
            AxisItem? pinned = null;
            foreach (var item in items)
            {
                if (item.Level == level && item.Position <= accumulated)
                {
                    if (pinned is null || item.Position > pinned.Value.Position)
                    {
                        pinned = item;
                    }
                }
            }

            if (pinned is null)
            {
                continue;
            }

            var pinPosition = accumulated;
            AxisItem? nextBlockingItem = null;
            foreach (var item in items)
            {
                if (item.Level > level || item.Position <= pinned.Value.Position)
                {
                    continue;
                }

                if (nextBlockingItem is null || item.Position < nextBlockingItem.Value.Position)
                {
                    nextBlockingItem = item;
                }
            }

            if (nextBlockingItem is not null)
            {
                var pushedPinPosition = nextBlockingItem.Value.Position - pinned.Value.Size;
                if (pushedPinPosition < pinPosition)
                {
                    pinPosition = pushedPinPosition;
                }
            }

            result[pinned.Value.Key] = pinPosition;
            accumulated = pinPosition + pinned.Value.Size;
        }

        return result;
    }
}
