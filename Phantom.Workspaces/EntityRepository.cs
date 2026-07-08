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

    public static async Task<EntityRepository> CreateAsync(
        RepositorySource repositorySource,
        string? userComputerProfileOverride = null)
    {
        var underlyingDataAccessLayer = await CreateUnderlyingDataAccessLayerAsync(repositorySource).ConfigureAwait(false);
        var isWebSource = repositorySource is WebRepositorySource or DevTunnelNameRepositorySource;
        IDataAccessLayer innerDataAccessLayer;
        if (isWebSource)
        {
            innerDataAccessLayer = underlyingDataAccessLayer;
        }
        else
        {
            var schemaAccessor = new SchemaAccessor(underlyingDataAccessLayer);
            innerDataAccessLayer = new MergeProcessingDataAccessLayer(
                new ReferentialIntegrityDataAccessLayer(
                    new SchemaValidatingDataAccessLayer(underlyingDataAccessLayer, schemaAccessor),
                    schemaAccessor));
        }
        if (!isWebSource)
        {
            await EnsureSeedDataIfNeededAsync(innerDataAccessLayer).ConfigureAwait(false);
        }

        var coreDataAccessLayer = new ScheduleDataAccessLayer(innerDataAccessLayer);
        var workspaceEntitySession = await WorkspaceEntitySessionBootstrapper.InitializeAsync(coreDataAccessLayer, userComputerProfileOverride).ConfigureAwait(false);
        var repository = new EntityRepository(repositorySource, coreDataAccessLayer, workspaceEntitySession);
        return repository;
    }

    private static async Task<IDataAccessLayer> CreateUnderlyingDataAccessLayerAsync(
        RepositorySource repositorySource)
    {
        return repositorySource switch
        {
            WebRepositorySource web => CreateWebDataAccessLayer(web),
            DevTunnelNameRepositorySource devTunnel => await CreateDevTunnelNameDataAccessLayerAsync(devTunnel).ConfigureAwait(false),
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
        string? devTunnelAccessToken = null;
        Func<string?>? devTunnelAccessTokenResolver = null;
        if (repositorySource.UseGitHubAuthToken)
        {
            devTunnelAccessToken = Phantom.Workspaces.Llm.GitHubAuthTokenResolver.Resolve();
            if (string.IsNullOrWhiteSpace(devTunnelAccessToken))
            {
                throw new InvalidOperationException(
                    "A GitHub authentication token is required to connect to the dev tunnel endpoint. Set the GITHUB_TOKEN environment variable or sign in with 'gh auth login'.");
            }

            devTunnelAccessTokenResolver = () => Phantom.Workspaces.Llm.GitHubAuthTokenResolver.Resolve();
        }

        return new WebClientDataAccessLayer(repositorySource.Endpoint, devTunnelAccessToken, devTunnelAccessTokenResolver);
    }

    private static async Task<IDataAccessLayer> CreateDevTunnelNameDataAccessLayerAsync(
        DevTunnelNameRepositorySource repositorySource)
    {
        // Discover the relay endpoint (and forwarded port) from the tunnel name, and keep it fresh:
        // on a connection drop the reconnecting layer re-resolves the tunnel (picking up a changed
        // port) and reconnects with bounded backoff, without restarting the workspace. The connect
        // token is fetched automatically by the Management API (Private mode) or absent (Anonymous).
        var resolver = new Services.DevTunnel.DevTunnelServiceFactory()
            .CreateEndpointResolver();

        var reconnectingDataAccessLayer = new Services.DevTunnel.ReconnectingWebDataAccessLayer(
            resolveEndpointAsync: cancellationToken => resolver.ResolveAsync(
                repositorySource.TunnelName,
                repositorySource.AccessMode,
                cancellationToken),
            buildDataAccessLayer: resolution => new WebClientDataAccessLayer(
                resolution.BaseUri.ToString(),
                resolution.TunnelAuthToken),   // null for Anonymous; connect-token for Private
            delayScheduler: Services.DevTunnel.RealDelayScheduler.Instance);

        await reconnectingDataAccessLayer.StartAsync().ConfigureAwait(false);
        return reconnectingDataAccessLayer;
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
}
