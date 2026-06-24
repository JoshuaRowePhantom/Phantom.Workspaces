using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces;

/// <summary>
/// Builds an entity's status badges by scanning its fields (across all its entity types) for the
/// <c>x-field-status</c> annotation. For each annotated field whose value is a non-empty string, a
/// <see cref="StatusBadgeModel"/> is produced, colored by <see cref="StatusColorSelector"/>. Fields
/// without the annotation, or with a missing/empty value, never produce a badge.
/// </summary>
/// <remarks>
/// Discovery is asynchronous because each field's type (and therefore its status annotation) is
/// resolved through <see cref="FieldTypeResolver"/>, unlike interest badges which are projected
/// synchronously from the entity's relationships.
/// </remarks>
public static class StatusBadgeProjector
{
    public static async Task<IReadOnlyList<StatusBadgeModel>> ProjectAsync(
        FieldTypeResolver fieldTypeResolver,
        StatusColorSelector statusColorSelector,
        JsonElement entityData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fieldTypeResolver);
        ArgumentNullException.ThrowIfNull(statusColorSelector);

        if (entityData.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<StatusBadgeModel>();
        }

        var fieldNames = await fieldTypeResolver
            .EnumerateObjectFieldNamesAsync(entityData, Array.Empty<string>(), entityData, cancellationToken)
            .ConfigureAwait(false);

        var badges = new List<StatusBadgeModel>();
        foreach (var fieldName in fieldNames)
        {
            if (!entityData.TryGetProperty(fieldName, out var fieldValue)
                || fieldValue.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var statusValue = fieldValue.GetString();
            if (string.IsNullOrEmpty(statusValue))
            {
                continue;
            }

            IReadOnlyList<string> fieldPath = new[] { fieldName };
            var resolvedType = await fieldTypeResolver
                .ResolveFieldTypeAsync(entityData, fieldPath, fieldValue, cancellationToken)
                .ConfigureAwait(false);

            if (resolvedType.FieldStatus is not { } fieldStatus)
            {
                continue;
            }

            var brushKey = statusColorSelector.SelectStatusBrushKey(statusValue, fieldStatus);
            badges.Add(new StatusBadgeModel(
                statusValue,
                brushKey,
                $"{fieldName}: {statusValue}"));
        }

        return badges;
    }
}
