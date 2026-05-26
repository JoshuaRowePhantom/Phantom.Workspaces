using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Agent.Gui.Converters;

public sealed class RenderableContentFilterConverter : IValueConverter
{
    public static readonly RenderableContentFilterConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IEnumerable<AIContent> contents)
        {
            return Array.Empty<AIContent>();
        }

        return contents
            .Where(static content => content is not TextReasoningContent)
            .Where(static content => content is not DataContent data || !IsImageMediaType(data.MediaType))
            .ToArray();
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static bool IsImageMediaType(string? mediaType)
        => !string.IsNullOrWhiteSpace(mediaType) && mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
