using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Agent.Gui.Converters;

public sealed class FunctionContentFilterConverter : IValueConverter
{
    public static readonly FunctionContentFilterConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<AIContent> contents)
        {
            return Array.Empty<AIContent>();
        }

        return contents
            .Where(static content => content is FunctionCallContent or FunctionResultContent)
            .ToArray();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
