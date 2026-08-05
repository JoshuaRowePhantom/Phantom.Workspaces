using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Phantom.Workspaces.Converters;

/// <summary>
/// Converts a nullable fraction to a bool indicating whether the fraction is known
/// (non-null). Used in <c>UsageTrackerControl</c> to hide the green "remaining" bar and
/// show a distinct "unknown limit" indicator when <see cref="Phantom.Workspaces.Models.UsageMetric.FractionUsed"/>
/// is null — otherwise the row would visually read as "100% filled with green" (see #1159).
/// Use <see cref="Instance"/> for "known" semantics and <see cref="Inverse"/> for "unknown".
/// </summary>
public sealed class FractionKnownConverter : IValueConverter
{
    public static readonly FractionKnownConverter Instance = new(known: true);
    public static readonly FractionKnownConverter Inverse = new(known: false);

    private readonly bool known;

    private FractionKnownConverter(bool known)
    {
        this.known = known;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isKnown = value is double;
        return isKnown == this.known;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
