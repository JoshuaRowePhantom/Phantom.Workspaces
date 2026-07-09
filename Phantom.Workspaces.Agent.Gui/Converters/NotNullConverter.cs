using System.Globalization;
using Avalonia.Data.Converters;

namespace Phantom.Workspaces.Agent.Gui.Converters;

/// <summary>
/// Value converter that returns true if the value is not null.
/// </summary>
public static class NotNullConverter
{
    public static readonly IValueConverter Instance = new LambdaConverter(v => v is not null);

    private sealed class LambdaConverter(Func<object?, bool> convert) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => convert(value);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
