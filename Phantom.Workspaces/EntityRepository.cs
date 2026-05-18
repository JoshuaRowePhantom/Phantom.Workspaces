using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces;

public sealed class EntityRepository
{
    private readonly IDataAccessLayer underlyingDataAccessLayer;

    private EntityRepository(
        RepositorySource repositorySource,
        IDataAccessLayer underlyingDataAccessLayer)
    {
        this.RepositorySource = repositorySource;
        this.underlyingDataAccessLayer = underlyingDataAccessLayer;
        this.DataAccessLayer = new MergeProcessingDataAccessLayer(
            new ReferentialIntegrityDataAccessLayer(
                new SchemaValidatingDataAccessLayer(this.underlyingDataAccessLayer)));
    }

    public RepositorySource RepositorySource { get; }

    public IDataAccessLayer DataAccessLayer { get; }

    public static EntityRepository Create(
        RepositorySource repositorySource)
    {
        return CreateAsync(repositorySource).GetAwaiter().GetResult();
    }

    public static async Task<EntityRepository> CreateAsync(
        RepositorySource repositorySource)
    {
        var underlyingDataAccessLayer = CreateUnderlyingDataAccessLayer(repositorySource);
        var repository = new EntityRepository(repositorySource, underlyingDataAccessLayer);
        await repository.EnsureSeedDataIfNeededAsync();
        return repository;
    }

    public async Task<IReadOnlyDictionary<EntityId, EntitySnapshot>> ExportEntitySnapshotsAsync(
        CancellationToken cancellationToken = default)
    {
        var exportResult = await this.DataAccessLayer.ExportAsync(new ExportRequest(), cancellationToken);
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

    public EntitySnapshot? TryGetEntityByName(
        IReadOnlyDictionary<EntityId, EntitySnapshot> snapshots,
        EntityName entityName)
    {
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

    private static IDataAccessLayer CreateUnderlyingDataAccessLayer(
        RepositorySource repositorySource)
    {
        return repositorySource.SourceType switch
        {
            RepositorySourceType.LocalGit => new GitDataAccessLayer(repositorySource.RawValue),
            _ => new InMemoryDataAccessLayer(),
        };
    }

    private async Task EnsureSeedDataIfNeededAsync()
    {
        var errors = await new SchemaPopulator(this.DataAccessLayer).Populate();
        if (errors.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Failed to populate repository schemas: {string.Join(" | ", errors.Select(static error => error.Message))}");
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
