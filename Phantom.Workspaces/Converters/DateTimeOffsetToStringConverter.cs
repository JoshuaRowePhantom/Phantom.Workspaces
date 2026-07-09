using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Phantom.Workspaces.Converters;

public sealed class DateTimeOffsetToStringConverter : IValueConverter
{
    public static readonly DateTimeOffsetToStringConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTimeOffset dateTimeOffset)
        {
            if (dateTimeOffset == DateTimeOffset.MinValue)
            {
                return string.Empty;
            }

            var now = DateTimeOffset.Now;
            var diff = now - dateTimeOffset;

            if (diff.TotalHours < 24)
            {
                return dateTimeOffset.ToLocalTime().ToString("HH:mm", culture);
            }
            else if (diff.TotalDays < 7)
            {
                return dateTimeOffset.ToLocalTime().ToString("ddd HH:mm", culture);
            }
            else if (dateTimeOffset.Year == now.Year)
            {
                return dateTimeOffset.ToLocalTime().ToString("MMM dd HH:mm", culture);
            }
            else
            {
                return dateTimeOffset.ToLocalTime().ToString("yyyy-MM-dd", culture);
            }
        }

        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
