using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ViewModels;

/// <summary>
/// Production <see cref="IEntityReferenceSearch"/> backed by the <see cref="EntityBroker"/>. Resolves a
/// referenced entity id to its display name (so relationship participants and other entity-id fields
/// render as the related entity rather than a raw id), and searches candidate entities by type for the
/// edit-mode picker.
/// </summary>
public sealed class EntityReferenceSearch : IEntityReferenceSearch
{
    private const int MaxResults = 25;

    private readonly EntityBroker entityBroker;

    public EntityReferenceSearch(
        EntityBroker entityBroker)
    {
        this.entityBroker = entityBroker;
    }

    public async Task<EntityReferenceCandidate?> ResolveAsync(
        string entityId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return null;
        }

        EntityId id;
        try
        {
            id = new EntityId(entityId);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }

        var entities = await this.entityBroker.GetEntitiesAsync(new[] { id }, cancellationToken).ConfigureAwait(true);
        var entity = entities.FirstOrDefault(candidate => candidate.EntityId == id) ?? entities.FirstOrDefault();
        return entity is null ? null : ToCandidate(entity);
    }

    public async Task<IReadOnlyList<EntityReferenceCandidate>> SearchAsync(
        string searchText,
        IReadOnlyCollection<string> entityTypes,
        CancellationToken cancellationToken = default)
    {
        var typeNames = entityTypes is { Count: > 0 }
            ? entityTypes
            : new[] { "entity" };

        var snapshotsById = new Dictionary<EntityId, EntitySnapshot>();
        foreach (var typeName in typeNames.Distinct(StringComparer.Ordinal))
        {
            var queryResult = await this.entityBroker.EntityRepository.DataAccessLayer.QueryAsync(
                new QueryRequest
                {
                    Clauses =
                    [
                        new TopLevelQueryClause
                        {
                            ClauseIdentifier = new QueryClauseIdentifier { Value = $"entity-reference-{typeName}" },
                            Clause = new EntityTypeQueryClause
                            {
                                EntityTypeNames = new EntityTypeNameSet { Values = [typeName] },
                            },
                        },
                    ],
                    Timestamps = [null],
                },
                cancellationToken).ConfigureAwait(true);

            foreach (var snapshot in queryResult.Batches.SelectMany(batch => batch.Entities))
            {
                snapshotsById.TryAdd(snapshot.EntityId, snapshot);
            }
        }

        // Case-insensitive matching is applied to the user-typed search text only.
        var candidates = snapshotsById.Values
            .Select(ToCandidate)
            .Where(candidate => string.IsNullOrWhiteSpace(searchText)
                || candidate.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                || candidate.Names.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .ToArray();

        return candidates;
    }

    private static EntityReferenceCandidate ToCandidate(
        SubscribedEntityViewModel entity)
        => new(
            entity.EntityId.ToString(),
            entity.DisplayName,
            FormatNames(entity.Snapshot));

    private static EntityReferenceCandidate ToCandidate(
        EntitySnapshot snapshot)
    {
        var displayName = EntityPresentation.GetDisplayName(snapshot);
        return new EntityReferenceCandidate(snapshot.EntityId.ToString(), displayName, FormatNames(snapshot));
    }

    private static string FormatNames(
        EntitySnapshot snapshot)
    {
        if (snapshot.Data is JsonElement data
            && EntityListNodeViewModel.TryGetPrimaryName(data, out var entityName))
        {
            return string.Join("/", entityName.Components);
        }

        return snapshot.EntityId.ToString();
    }
}
