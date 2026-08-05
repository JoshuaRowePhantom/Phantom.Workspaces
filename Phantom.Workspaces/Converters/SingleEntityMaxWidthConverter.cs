using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Phantom.Workspaces.Converters;

/// <summary>
/// Issue #1066: computes the maximum width of the single-entity card host as the smaller of
/// (a) the hosting <c>ScrollViewer</c>'s viewport width (so content caps to the viewport, wraps,
/// and never overflows when wide) and (b) roughly one third of the hosting pane width (so the
/// card stays centered with a ~1/3 maximum width). Values that are unusable (NaN, non-positive,
/// or infinite) are ignored; if neither input is usable a sensible fixed cap is returned.
/// </summary>
public sealed class SingleEntityMaxWidthConverter : IMultiValueConverter
{
    public static readonly SingleEntityMaxWidthConverter Instance = new();

    private const double OneThird = 1.0 / 3.0;
    private const double FallbackMaxWidth = 480.0;

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var viewport = ToUsableWidth(values.Count > 0 ? values[0] : null);
        var paneWidth = ToUsableWidth(values.Count > 1 ? values[1] : null);
        var oneThird = paneWidth is double p ? p * OneThird : (double?)null;

        if (viewport is double v && oneThird is double t)
        {
            return Math.Min(v, t);
        }

        return viewport ?? oneThird ?? FallbackMaxWidth;
    }

    private static double? ToUsableWidth(object? value)
    {
        if (value is double d && !double.IsNaN(d) && !double.IsInfinity(d) && d > 0.0)
        {
            return d;
        }

        return null;
    }
}
