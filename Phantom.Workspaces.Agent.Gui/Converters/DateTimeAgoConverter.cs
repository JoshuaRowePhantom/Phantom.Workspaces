using System.Globalization;
using Avalonia.Data.Converters;

namespace Phantom.Workspaces.Agent.Gui.Converters;

/// <summary>
/// Converts a <see cref="DateTime"/> (or <see cref="Nullable{DateTime}"/>) into a human-readable
/// relative-time ("ago") string, e.g. "just now", "3 minutes ago", "1 hour ago", "5 days ago".
/// The value is interpreted relative to the current time; the comparison reference matches the
/// <see cref="DateTime.Kind"/> of the supplied value (UTC values compare against
/// <see cref="DateTime.UtcNow"/>, otherwise against <see cref="DateTime.Now"/>).
/// </summary>
/// <remarks>
/// Because the produced text is relative to "now", it only refreshes when the binding re-evaluates.
/// A periodic refresh is intentionally out of scope.
/// </remarks>
public static class DateTimeAgoConverter
{
    public static readonly IValueConverter Instance = new AgoConverter();

    /// <summary>
    /// Produces the relative-time string for <paramref name="value"/> compared to now.
    /// </summary>
    public static string ToRelativeString(DateTime value)
    {
        var now = value.Kind == DateTimeKind.Utc ? DateTime.UtcNow : DateTime.Now;
        var delta = now - value;

        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta.TotalSeconds < 60)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 60)
        {
            var minutes = (int)delta.TotalMinutes;
            return $"{minutes} {(minutes == 1 ? "minute" : "minutes")} ago";
        }

        if (delta.TotalHours < 24)
        {
            var hours = (int)delta.TotalHours;
            return $"{hours} {(hours == 1 ? "hour" : "hours")} ago";
        }

        var days = (int)delta.TotalDays;
        return $"{days} {(days == 1 ? "day" : "days")} ago";
    }

    private sealed class AgoConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value switch
            {
                null => string.Empty,
                DateTime dateTime => ToRelativeString(dateTime),
                _ => string.Empty,
            };

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
