using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

namespace Phantom.Workspaces.Converters;

public sealed class EntityTypeColorConverter : IValueConverter
{
    public static EntityTypeColorConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<string> names)
        {
            return Brushes.Transparent;
        }

        var typeNames = names
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        if (typeNames.Length == 0)
        {
            return Brushes.Transparent;
        }

        var key = $"{StatusColorSelector.PaletteBrushKeyPrefix}{(int)(StableHash(string.Join('\u0001', typeNames)) % StatusColorSelector.PaletteSize)}";
        var themeVariant = Application.Current?.ActualThemeVariant ?? ThemeVariant.Default;
        return Application.Current is { } app
            && app.TryGetResource(key, themeVariant, out var resource)
            && resource is IBrush brush
                ? brush
                : Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static uint StableHash(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }
}
