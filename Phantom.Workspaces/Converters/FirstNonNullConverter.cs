using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;

namespace Phantom.Workspaces.Converters;

/// <summary>
/// Returns the first non-null value from a MultiBinding. Used to fall back a
/// child binding to a parent binding when the child value is null (for example,
/// falling back a per-metric web URL to the owning account's settings URL).
/// </summary>
public sealed class FirstNonNullConverter : IMultiValueConverter
{
    public static readonly FirstNonNullConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        foreach (var v in values)
        {
            if (v is null) continue;
            if (v == Avalonia.AvaloniaProperty.UnsetValue) continue;
            if (v == Avalonia.Data.BindingOperations.DoNothing) continue;
            return v;
        }
        return null;
    }
}
