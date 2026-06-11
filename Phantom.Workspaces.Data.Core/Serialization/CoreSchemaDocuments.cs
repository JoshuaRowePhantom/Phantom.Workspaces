using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Workspaces.Data.Serialization;

public sealed record CoreLocalizedStringDocument
{
    public Dictionary<string, string> ValuesByLocale { get; init; } = new(StringComparer.Ordinal);

    public static CoreLocalizedStringDocument? Deserialize(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var valuesByLocale = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(property.Name)
                || string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                return null;
            }

            valuesByLocale[property.Name] = property.Value.GetString()!;
        }

        return new CoreLocalizedStringDocument
        {
            ValuesByLocale = valuesByLocale,
        };
    }

    public bool IsValid()
        => this.ValuesByLocale.TryGetValue("default", out var defaultValue)
           && !string.IsNullOrWhiteSpace(defaultValue);

    public string? GetValue(string? localeName)
    {
        if (!string.IsNullOrWhiteSpace(localeName)
            && this.ValuesByLocale.TryGetValue(localeName, out var localizedValue)
            && !string.IsNullOrWhiteSpace(localizedValue))
        {
            return localizedValue;
        }

        return this.ValuesByLocale.TryGetValue("default", out var defaultValue)
            && !string.IsNullOrWhiteSpace(defaultValue)
            ? defaultValue
            : null;
    }

    public CoreLocalizedStringDocument SetValue(string? localeName, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Localized value cannot be null or whitespace.", nameof(value));
        }

        var targetLocaleName = string.IsNullOrWhiteSpace(localeName) ? "default" : localeName;
        var copy = new Dictionary<string, string>(this.ValuesByLocale, StringComparer.Ordinal)
        {
            [targetLocaleName] = value,
        };
        return this with
        {
            ValuesByLocale = copy,
        };
    }
}

public sealed record CoreEntityNameDocument
{
    public required string[] Components { get; init; }

    public static CoreEntityNameDocument? Deserialize(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var components = element.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
        if (components.Length == 0)
        {
            return null;
        }

        return new CoreEntityNameDocument
        {
            Components = components,
        };
    }

    public string ToCanonicalName() => JsonSerializer.Serialize(this.Components);
}

public sealed record CoreEntityReferenceDocument
{
    public string? EntityId { get; init; }

    public CoreEntityNameDocument? EntityName { get; init; }

    public static CoreEntityReferenceDocument? Deserialize(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out var entityId))
        {
            return new CoreEntityReferenceDocument
            {
                EntityId = entityId.ToString("D"),
            };
        }

        var entityNameDocument = CoreEntityNameDocument.Deserialize(element);
        if (entityNameDocument is not null)
        {
            return new CoreEntityReferenceDocument
            {
                EntityName = entityNameDocument,
            };
        }

        return null;
    }
}

public sealed record CoreEntityTypeSetDocument
{
    public required string[] EntityTypeNames { get; init; }

    public static CoreEntityTypeSetDocument? Deserialize(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var entityTypeNames = element.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
        if (entityTypeNames.Length == 0)
        {
            return null;
        }

        return new CoreEntityTypeSetDocument
        {
            EntityTypeNames = entityTypeNames,
        };
    }
}

public sealed record CoreTimestampDocument
{
    [JsonPropertyName("datetime")]
    public string DateTime { get; init; } = string.Empty;

    [JsonPropertyName("change-id")]
    public string ChangeId { get; init; } = string.Empty;

    public static CoreTimestampDocument? Deserialize(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
            ? EntityJsonSerializer.Deserialize(element, EntitySerializationJsonContext.Default.CoreTimestampDocument)
            : null;

    public bool IsValid()
        => !string.IsNullOrWhiteSpace(this.DateTime)
           && !string.IsNullOrWhiteSpace(this.ChangeId);
}

public sealed record CoreFieldPathDocument
{
    public required string[] Components { get; init; }

    public static CoreFieldPathDocument? Deserialize(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var components = element.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
        if (components.Length == 0)
        {
            return null;
        }

        return new CoreFieldPathDocument
        {
            Components = components,
        };
    }
}

public sealed record CoreSortFieldDocument
{
    [JsonPropertyName("field-path")]
    public JsonElement FieldPath { get; init; }

    [JsonPropertyName("sort-direction")]
    public string SortDirection { get; init; } = string.Empty;

    public static CoreSortFieldDocument? Deserialize(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
            ? EntityJsonSerializer.Deserialize(element, EntitySerializationJsonContext.Default.CoreSortFieldDocument)
            : null;

    public bool IsValid()
        => CoreFieldPathDocument.Deserialize(this.FieldPath) is not null
           && (this.SortDirection == "ascending" || this.SortDirection == "descending");
}
