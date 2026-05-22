using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Phantom.Workspaces.Agent.Gui.Converters;

public sealed class RoleToColorConverter : IValueConverter
{
    public static readonly RoleToColorConverter Instance = new();

    private static readonly IBrush UserBrush = new SolidColorBrush(Color.Parse("#6EC06E"));
    private static readonly IBrush AssistantBrush = new SolidColorBrush(Color.Parse("#6A9FD8"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? UserBrush : AssistantBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
