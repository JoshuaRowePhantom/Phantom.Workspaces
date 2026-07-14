using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Phantom.Workspaces.Converters;

/// <summary>
/// Converts a nullable <see cref="double"/> fraction (0.0–1.0) to a pixel width by multiplying
/// by the converter parameter (total bar width). Returns 0.0 when the fraction is null.
/// </summary>
public sealed class FractionToWidthConverter : IValueConverter
{
    public static readonly FractionToWidthConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value is double d ? d : 0.0;
        var totalWidth = parameter is string s && double.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var w)
            ? w
            : parameter is double dw ? dw : 120.0;
        return Math.Max(0.0, Math.Min(totalWidth, fraction * totalWidth));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
