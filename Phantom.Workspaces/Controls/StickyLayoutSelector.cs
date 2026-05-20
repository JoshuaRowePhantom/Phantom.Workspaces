using System.Collections.Generic;
using System.Linq;

namespace Phantom.Workspaces.Controls;

/// <summary>
/// An item in a scrollable container that may participate in sticky pinning.
/// Key is any object used to identify the item (string in tests, Control in the engine).
/// </summary>
public readonly record struct StickyItemMeasurement(
    object Key,
    double Top,
    double Left,
    double Height,
    double Width,
    int? VerticalLevel,
    int? HorizontalLevel);

/// <summary>
/// The target visual position for a sticky-pinned item.
/// PinY/PinX are null if the item is not pinned on that axis.
/// </summary>
public readonly record struct StickyPinTarget(
    object Key,
    double? PinY,
    double? PinX);

/// <summary>
/// Pure (no Avalonia types) sticky layout algorithm.
/// For each axis, working from level 0 upward, pins the item with the highest position
/// that has scrolled at or past the accumulated anchor for that level.
/// </summary>
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

    private static Dictionary<object, double> ComputeAxisPins(
        IReadOnlyList<AxisItem> items)
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

            result[pinned.Value.Key] = accumulated;
            accumulated += pinned.Value.Size;
        }

        return result;
    }
}
