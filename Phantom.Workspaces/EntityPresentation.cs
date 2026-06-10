using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces;

public static class EntityPresentation
{
    public static string GetDisplayName(
        EntitySnapshot snapshot)
    {
        if (snapshot.Data is JsonElement data)
        {
            return ReadLocalString(data, "display-name")
                ?? ReadLocalString(data, "title")
                ?? ReadPrimaryName(data)
                ?? snapshot.EntityId.ToString();
        }

        return snapshot.EntityId.ToString();
    }

    public static string GetEntityType(
        EntitySnapshot snapshot)
    {
        if (snapshot.Data is JsonElement data
            && data.TryGetProperty("entity-types", out var types)
            && types.ValueKind == JsonValueKind.Array)
        {
            foreach (var type in types.EnumerateArray())
            {
                if (type.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(type.GetString()))
                {
                    return type.GetString()!;
                }
            }
        }

        return "entity";
    }

    public static IReadOnlyCollection<EntityDisplayItemViewModel> GetDisplayItems(
        EntitySnapshot snapshot)
    {
        var items = new List<EntityDisplayItemViewModel>();
        if (snapshot.Data is not JsonElement data)
        {
            return items;
        }

        var markdown = GetMarkdownText(data);
        if (!string.IsNullOrWhiteSpace(markdown))
        {
            items.Add(new EntityDisplayItemViewModel(markdown));
        }

        return items;
    }

    private static string? ReadLocalString(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        if (property.ValueKind == JsonValueKind.Object)
        {
            var locale = CultureInfo.CurrentUICulture.Name;
            if (property.TryGetProperty(locale, out var localizedValue)
                && localizedValue.ValueKind == JsonValueKind.String)
            {
                return localizedValue.GetString();
            }

            var neutralLocale = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (property.TryGetProperty(neutralLocale, out localizedValue)
                && localizedValue.ValueKind == JsonValueKind.String)
            {
                return localizedValue.GetString();
            }

            if (property.TryGetProperty("default", out var defaultValue)
                && defaultValue.ValueKind == JsonValueKind.String)
            {
                return defaultValue.GetString();
            }
        }

        return null;
    }

    private static string? ReadPrimaryName(
        JsonElement element)
    {
        if (!element.TryGetProperty("names", out var names)
            || names.ValueKind != JsonValueKind.Array
            || names.GetArrayLength() == 0)
        {
            return null;
        }

        var first = names[0];
        if (first.ValueKind == JsonValueKind.String)
        {
            return first.GetString();
        }

        if (first.ValueKind == JsonValueKind.Array)
        {
            var parts = first.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString())
                .Where(static value => !string.IsNullOrWhiteSpace(value));
            return string.Join("/", parts!);
        }

        return null;
    }

    private static string? GetMarkdownText(
        JsonElement entityData)
    {
        if (entityData.TryGetProperty("markdown", out var markdown)
            && markdown.ValueKind == JsonValueKind.String)
        {
            return markdown.GetString();
        }

        if (!entityData.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Object
            || !content.TryGetProperty("default", out var defaultContent)
            || defaultContent.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (defaultContent.TryGetProperty("content", out var inlineContent)
            && inlineContent.ValueKind == JsonValueKind.Object
            && inlineContent.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString();
        }
        
        return null;
    }
}
