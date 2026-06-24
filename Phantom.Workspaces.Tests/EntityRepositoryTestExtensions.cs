using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Test-only helpers that enumerate the entire repository. Production code must
/// never enumerate the whole store; these helpers exist purely so tests can
/// assert over all seeded/updated entities.
/// </summary>
internal static class EntityRepositoryTestExtensions
{
    public static async Task<IReadOnlyDictionary<EntityId, EntitySnapshot>> ExportEntitySnapshotsAsync(
        this EntityRepository repository,
        CancellationToken cancellationToken = default)
    {
#pragma warning disable CS0618
        var exportResult = await repository.DataAccessLayer.ExportAsync(new ExportRequest(), cancellationToken);
#pragma warning restore CS0618
        return exportResult.ChangeBatches
            .SelectMany(static batch => batch.Entities)
            .GroupBy(static snapshot => snapshot.EntityId)
            .ToDictionary(
                static group => group.Key,
                static group => (EntitySnapshot)group
                    .OrderByDescending(static snapshot => snapshot.ModifiedTime.DateTime)
                    .ThenByDescending(static snapshot => snapshot.ModifiedTime.ChangeId, StringComparer.Ordinal)
                    .First());
    }

    public static EntitySnapshot? TryGetEntityByName(
        this EntityRepository repository,
        IReadOnlyDictionary<EntityId, EntitySnapshot> snapshots,
        EntityName entityName)
    {
        _ = repository;
        foreach (var snapshot in snapshots.Values)
        {
            if (snapshot.Data is not JsonElement data)
            {
                continue;
            }

            if (!TryGetEntityNames(data, out var names))
            {
                continue;
            }

            if (names.Any(name => name == entityName))
            {
                return snapshot;
            }
        }

        return null;
    }

    private static bool TryGetEntityNames(
        JsonElement entityData,
        out IReadOnlyCollection<EntityName> names)
    {
        var resolved = new List<EntityName>();
        if (!entityData.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array)
        {
            names = resolved;
            return false;
        }

        foreach (var nameElement in namesElement.EnumerateArray())
        {
            var parsedEntityName = nameElement.TryReadEntityName();
            if (parsedEntityName is not null)
            {
                resolved.Add(parsedEntityName.Value);
            }
        }

        names = resolved;
        return resolved.Count > 0;
    }
}
