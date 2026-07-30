using System;
using System.Text.Json;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Recursively tests a <see cref="JsonElement"/> for a case-insensitive match of a query string
/// against value nodes only. Property names/keys and structural punctuation are never tested — this
/// deliberately avoids false positives from searching a serialized-JSON blob where key text would
/// match (e.g. "name" matching every <c>"name":</c> key).
/// </summary>
public static class JsonValueMatcher
{
    public static bool MatchesJsonValues(JsonElement element, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    // NOTE: property.Name (the key) is intentionally NOT tested.
                    if (MatchesJsonValues(property.Value, query))
                    {
                        return true;
                    }
                }
                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (MatchesJsonValues(item, query))
                    {
                        return true;
                    }
                }
                return false;

            case JsonValueKind.String:
                return element.GetString() is { } s
                    && s.Contains(query, StringComparison.OrdinalIgnoreCase);

            case JsonValueKind.Number:
                return element.GetRawText()
                    .Contains(query, StringComparison.OrdinalIgnoreCase);

            case JsonValueKind.True:
                return "true".Contains(query, StringComparison.OrdinalIgnoreCase);

            case JsonValueKind.False:
                return "false".Contains(query, StringComparison.OrdinalIgnoreCase);

            default:
                return false;
        }
    }

    public static bool MatchesJsonValues(JsonElement? element, string query)
    {
        return element is JsonElement e && MatchesJsonValues(e, query);
    }
}
