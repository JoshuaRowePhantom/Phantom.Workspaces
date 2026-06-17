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
using Phantom.Workspaces.Data.Web.Client;

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
        var underlyingDataAccessLayer = await CreateUnderlyingDataAccessLayerAsync(repositorySource).ConfigureAwait(false);
        var isWebSource = repositorySource is WebRepositorySource;
        var coreDataAccessLayer = isWebSource
            ? underlyingDataAccessLayer
            : new MergeProcessingDataAccessLayer(
                new ReferentialIntegrityDataAccessLayer(
                    new SchemaValidatingDataAccessLayer(underlyingDataAccessLayer)));
        if (!isWebSource)
        {
            await EnsureSeedDataIfNeededAsync(coreDataAccessLayer).ConfigureAwait(false);
        }

        var workspaceEntitySession = await WorkspaceEntitySessionBootstrapper.InitializeAsync(coreDataAccessLayer).ConfigureAwait(false);
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

    private static async Task<IDataAccessLayer> CreateUnderlyingDataAccessLayerAsync(
        RepositorySource repositorySource)
    {
        return repositorySource switch
        {
            WebRepositorySource web => CreateWebDataAccessLayer(web),
            LocalGitRepositorySource git => new GitDataAccessLayer(git.Path),
            MongoDbRepositorySource mongo => await CreateMongoDbDataAccessLayerAsync(mongo).ConfigureAwait(false),
            _ => new InMemoryDataAccessLayer(),
        };
    }

    private static IDataAccessLayer CreateWebDataAccessLayer(WebRepositorySource repositorySource)
    {
        if (string.IsNullOrWhiteSpace(repositorySource.Endpoint))
        {
            throw new InvalidOperationException("Web repository source requires an endpoint URL.");
        }

        // Dev tunnel access authorizes with the GitHub auth token (GITHUB_TOKEN env var, else
        // `gh auth token`); plain web access uses no tunnel-authorization header.
        var devTunnelAccessToken = repositorySource.UseGitHubAuthToken
            ? Phantom.Workspaces.Llm.GitHubAuthTokenResolver.Resolve()
            : null;

        return new WebClientDataAccessLayer(repositorySource.Endpoint, devTunnelAccessToken);
    }

    private static async Task<IDataAccessLayer> CreateMongoDbDataAccessLayerAsync(
        MongoDbRepositorySource repositorySource)
    {
        if (string.IsNullOrWhiteSpace(repositorySource.ContainerName))
        {
            throw new InvalidOperationException("MongoDb container name is required for MongoDb repository sources.");
        }

        if (string.IsNullOrWhiteSpace(repositorySource.RootCollectionName))
        {
            throw new InvalidOperationException("MongoDb root collection name is required for MongoDb repository sources.");
        }

        var mongoDbDataDirectory = repositorySource.DataDirectory ?? string.Empty;
        var mongoDbDatabaseName = string.IsNullOrWhiteSpace(repositorySource.DatabaseName)
            ? "phantom-workspaces"
            : repositorySource.DatabaseName;

        var connectionDefinition = MongoDbConnectionDefinition.CreateContainer(
            repositorySource.ContainerName,
            mongoDbDataDirectory,
            mongoDbDatabaseName,
            repositorySource.RootCollectionName,
            repositorySource.HostPort);
        var mongoDbConnectionBroker = new MongoDbConnectionBroker();
        var mongoDbClient = await mongoDbConnectionBroker.GetClientAsync(connectionDefinition).ConfigureAwait(false);
        var mongoDbDatabase = mongoDbClient.GetDatabase(mongoDbDatabaseName);
        return new MongoDbEntityDataAccessLayer(mongoDbDatabase, repositorySource.RootCollectionName);
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
