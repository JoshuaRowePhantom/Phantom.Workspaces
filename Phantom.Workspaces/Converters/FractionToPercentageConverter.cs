using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Phantom.Workspaces.Converters;

/// <summary>
/// Converts a nullable <see cref="double"/> fraction (0.0–1.0) to a percentage string (e.g. "50%").
/// Returns "—" when the fraction is null (total is zero).
/// </summary>
public sealed class FractionToPercentageConverter : IValueConverter
{
    public static readonly FractionToPercentageConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "—";
        if (value is double d) return d.ToString("P0", culture);
        return "—";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
