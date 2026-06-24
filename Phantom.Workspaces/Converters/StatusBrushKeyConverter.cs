using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

namespace Phantom.Workspaces.Converters;

/// <summary>
/// Resolves a status badge's theme resource key (produced by <see cref="StatusColorSelector"/>) to the
/// actual brush at render time, so all status colors stay centralized in the styles and never appear
/// inline. Returns <see cref="Brushes.Transparent"/> when the key cannot be resolved.
/// </summary>
public sealed class StatusBrushKeyConverter : IValueConverter
{
    public static StatusBrushKeyConverter Instance { get; } = new();

    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        if (value is not string resourceKey
            || string.IsNullOrEmpty(resourceKey))
        {
            return Brushes.Transparent;
        }

        var themeVariant = Application.Current?.ActualThemeVariant ?? ThemeVariant.Default;
        if (Application.Current is { } application
            && application.TryGetResource(resourceKey, themeVariant, out var resource)
            && resource is IBrush brush)
        {
            return brush;
        }

        return Brushes.Transparent;
    }

    public object? ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
