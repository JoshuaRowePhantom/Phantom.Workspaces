using System;
using System.Collections.Generic;
using System.Linq;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Sort key for a field rendered on an entity card, implementing the entity-editor field ordering:
/// fields with an absolute order render before all other fields (sorted by that absolute value);
/// the remaining fields are grouped by their contributing entity type's display order, then by
/// type name, then by the field's relative order, then by field name.
/// </summary>
public readonly record struct FieldOrderingKey(
    int Group,
    double Primary,
    string TypeName,
    double Relative,
    string Name) : IComparable<FieldOrderingKey>
{
    public int CompareTo(FieldOrderingKey other)
    {
        var groupComparison = this.Group.CompareTo(other.Group);
        if (groupComparison != 0)
        {
            return groupComparison;
        }

        var primaryComparison = this.Primary.CompareTo(other.Primary);
        if (primaryComparison != 0)
        {
            return primaryComparison;
        }

        var typeComparison = string.CompareOrdinal(this.TypeName, other.TypeName);
        if (typeComparison != 0)
        {
            return typeComparison;
        }

        var relativeComparison = this.Relative.CompareTo(other.Relative);
        return relativeComparison != 0
            ? relativeComparison
            : string.CompareOrdinal(this.Name, other.Name);
    }
}

public static class FieldOrdering
{
    /// <summary>
    /// Computes the ordering key for a field.
    /// </summary>
    /// <param name="fieldName">The field name (final, stable tiebreaker).</param>
    /// <param name="absoluteOrder">The field's <c>x-absolute-entity-display-order</c>, if any.</param>
    /// <param name="relativeOrder">The field's <c>x-relative-entity-display-order</c> (default 0).</param>
    /// <param name="entityTypeName">The contributing entity type's name (groups a type's fields).</param>
    /// <param name="entityTypeDisplayOrder">The contributing entity type's <c>entity-display-order</c>.</param>
    public static FieldOrderingKey ComputeKey(
        string fieldName,
        double? absoluteOrder,
        double relativeOrder,
        string entityTypeName,
        double? entityTypeDisplayOrder)
    {
        if (absoluteOrder is double absolute)
        {
            // Absolute-ordered fields render before all others and are not grouped by entity type.
            return new FieldOrderingKey(0, absolute, string.Empty, relativeOrder, fieldName);
        }

        return new FieldOrderingKey(
            1,
            entityTypeDisplayOrder ?? double.MaxValue,
            entityTypeName,
            relativeOrder,
            fieldName);
    }

    /// <summary>
    /// Orders the supplied keyed items by their field ordering keys.
    /// </summary>
    public static IReadOnlyList<T> Order<T>(IEnumerable<T> items, Func<T, FieldOrderingKey> keySelector)
    {
        return items.OrderBy(keySelector).ToArray();
    }
}
