using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;

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

    /// <summary>
    /// Gets the primary entity type for display purposes. The base "entity" type that every
    /// entity declares is skipped so the domain type is returned; "entity" is returned only
    /// when it is the entity's sole declared type.
    /// </summary>
    /// <remarks>
    /// Do not use this for entity-type membership checks because an entity can have multiple types.
    /// Use <see cref="IsEntityType"/> when checking whether a specific type is present.
    /// </remarks>
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
                    && !string.IsNullOrWhiteSpace(type.GetString())
                    && !string.Equals(type.GetString(), "entity", StringComparison.Ordinal))
                {
                    return type.GetString()!;
                }
            }
        }

        return "entity";
    }

    public static bool IsEntityType(
        EntitySnapshot snapshot,
        string entityType)
    {
        if (string.IsNullOrWhiteSpace(entityType))
        {
            return false;
        }

        if (snapshot.Data is not JsonElement data
            || !data.TryGetProperty("entity-types", out var types)
            || types.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var type in types.EnumerateArray())
        {
            if (type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), entityType, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<string> GetNonAbstractEntityTypeNames(EntitySnapshot snapshot)
    {
        if (snapshot.Data is not JsonElement data
            || !data.TryGetProperty("entity-types", out var types)
            || types.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        // Preserve the entity-types declaration order so multi-typed cards compose their per-type
        // presentations in the order the entity declares (issue #1164). Only the base abstract
        // types "entity" and "abstract" are dropped; concrete types (e.g. "tool", "note") stay
        // in the order they appear on the entity.
        return types.EnumerateArray()
            .Where(static type => type.ValueKind == JsonValueKind.String)
            .Select(static type => type.GetString())
            .Where(static type => !string.IsNullOrWhiteSpace(type)
                && !string.Equals(type, "entity", StringComparison.Ordinal)
                && !string.Equals(type, "abstract", StringComparison.Ordinal))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
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
}
