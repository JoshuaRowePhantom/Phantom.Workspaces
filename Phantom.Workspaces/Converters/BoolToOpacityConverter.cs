using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Phantom.Workspaces.Converters;

/// <summary>
/// Returns 1.0 when the value is <see langword="true"/>, 0.0 otherwise.
/// Used to keep a visual element in the layout while hiding it when inactive.
/// </summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
