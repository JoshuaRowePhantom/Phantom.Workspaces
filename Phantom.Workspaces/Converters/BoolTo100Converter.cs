using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Phantom.Workspaces.Converters;

/// <summary>
/// Returns 100.0 when the value is <see langword="true"/>, 0.0 otherwise.
/// Used to bind a boolean state to a ProgressBar Value for notification-indicator styling.
/// </summary>
public sealed class BoolTo100Converter : IValueConverter
{
    public static readonly BoolTo100Converter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? 100.0 : 0.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
