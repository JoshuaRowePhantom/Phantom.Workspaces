using System.Globalization;
using System.Text.Json;
using Avalonia.Data.Converters;

namespace Phantom.Workspaces.Agent.Gui.Converters;

public sealed class ObjectToPrettyJsonConverter : IValueConverter
{
    public static readonly ObjectToPrettyJsonConverter Instance = new();

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            null => string.Empty,
            string s when string.IsNullOrWhiteSpace(s) => string.Empty,
            string s => TryPrettyPrintJson(s, out var pretty) ? pretty : s,
            JsonElement element => JsonSerializer.Serialize(element, PrettyOptions),
            _ => TrySerializePrettyJson(value),
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static string TrySerializePrettyJson(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value, PrettyOptions);
        }
        catch (NotSupportedException)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static bool TryPrettyPrintJson(string text, out string pretty)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            pretty = JsonSerializer.Serialize(document.RootElement, PrettyOptions);
            return true;
        }
        catch (JsonException)
        {
            pretty = string.Empty;
            return false;
        }
    }
}
