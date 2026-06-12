using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.MongoDB;
using Phantom.Workspaces.Data.Offline;

namespace Phantom.Workspaces;

public sealed class EntityRepository
{
    private readonly IDataAccessLayer coreDataAccessLayer;

    private EntityRepository(
        RepositorySource repositorySource,
        IDataAccessLayer coreDataAccessLayer,
        WorkspaceEntitySession workspaceEntitySession)
    {
        this.RepositorySource = repositorySource;
        this.coreDataAccessLayer = coreDataAccessLayer;
        this.WorkspaceEntitySession = workspaceEntitySession;
        this.DataAccessLayer = new WorkspaceEntitySessionDataAccessLayer(this.coreDataAccessLayer, this.WorkspaceEntitySession);
    }

    public RepositorySource RepositorySource { get; }

    public WorkspaceEntitySession WorkspaceEntitySession { get; }

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
        var coreDataAccessLayer = new MergeProcessingDataAccessLayer(
            new ReferentialIntegrityDataAccessLayer(
                new SchemaValidatingDataAccessLayer(underlyingDataAccessLayer)));
        await EnsureSeedDataIfNeededAsync(coreDataAccessLayer);
        var workspaceEntitySession = await WorkspaceEntitySessionBootstrapper.InitializeAsync(coreDataAccessLayer);
        var repository = new EntityRepository(repositorySource, coreDataAccessLayer, workspaceEntitySession);
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
            RepositorySourceType.MongoDb => CreateMongoDbDataAccessLayer(repositorySource),
            _ => new InMemoryDataAccessLayer(),
        };
    }

    private static IDataAccessLayer CreateMongoDbDataAccessLayer(
        RepositorySource repositorySource)
    {
        if (string.IsNullOrWhiteSpace(repositorySource.MongoDbContainerName))
        {
            throw new InvalidOperationException("MongoDb container name is required for MongoDb repository sources.");
        }

        if (string.IsNullOrWhiteSpace(repositorySource.MongoDbRootCollectionName))
        {
            throw new InvalidOperationException("MongoDb root collection name is required for MongoDb repository sources.");
        }

        var mongoDbDataDirectory = string.IsNullOrWhiteSpace(repositorySource.MongoDbDataDirectory)
            ? Path.GetFullPath(".\\mongo-data")
            : repositorySource.MongoDbDataDirectory;
        var mongoDbDatabaseName = string.IsNullOrWhiteSpace(repositorySource.MongoDbDatabaseName)
            ? "phantom-workspaces"
            : repositorySource.MongoDbDatabaseName;

        var connectionDefinition = MongoDbConnectionDefinition.CreateContainer(
            repositorySource.MongoDbContainerName,
            mongoDbDataDirectory,
            mongoDbDatabaseName,
            repositorySource.MongoDbRootCollectionName,
            repositorySource.MongoDbHostPort);
        var mongoDbConnectionBroker = new MongoDbConnectionBroker();
        var mongoDbClient = mongoDbConnectionBroker.GetClientAsync(connectionDefinition).AsTask().GetAwaiter().GetResult();
        var mongoDbDatabase = mongoDbClient.GetDatabase(mongoDbDatabaseName);
        return new MongoDbEntityDataAccessLayer(mongoDbDatabase, repositorySource.MongoDbRootCollectionName);
    }

    private static async Task EnsureSeedDataIfNeededAsync(
        IDataAccessLayer dataAccessLayer)
    {
        var errors = await new SchemaPopulator(dataAccessLayer).Populate();
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
