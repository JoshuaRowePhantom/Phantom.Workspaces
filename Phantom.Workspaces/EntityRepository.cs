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

    internal static Func<MongoDB.Driver.IMongoDatabase, string, MongoDbEntityDataAccessLayer>? TestMongoDbEntityDataAccessLayerFactory { get; set; }

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

    public IDataAccessLayer DataAccessLayer { get; private set; }

    internal void SetDataAccessLayerForTesting(IDataAccessLayer dataAccessLayer)
    {
        this.DataAccessLayer = dataAccessLayer;
    }

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
            // For MongoDB data access layers, ensure indexes and migrate schema before any reads
            if (underlyingDataAccessLayer is MongoDbEntityDataAccessLayer mongoDbDataAccessLayer)
            {
                await mongoDbDataAccessLayer.EnsureIndexesAsync().ConfigureAwait(false);
                await mongoDbDataAccessLayer.MigrateAsync().ConfigureAwait(false);
            }

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
        // `gh auth token`); plain web access uses no tunnel-authorization header. The shared
        // DevTunnelAuthenticationHandler (issue #1456) attaches/refreshes the token per request; this
        // client no longer knows about tunnels or tokens.
        if (!repositorySource.UseGitHubAuthToken)
        {
            return new WebClientDataAccessLayer(repositorySource.Endpoint);
        }

        var initialToken = Phantom.Workspaces.Llm.GitHubAuthTokenResolver.Resolve();
        if (string.IsNullOrWhiteSpace(initialToken))
        {
            throw new InvalidOperationException(
                "A GitHub authentication token is required to connect to the dev tunnel endpoint. Set the GITHUB_TOKEN environment variable or sign in with 'gh auth login'.");
        }

        var tokenProvider = new Services.DevTunnel.DelegateDevTunnelConnectTokenProvider(
            acquire: _ => new ValueTask<string?>(Phantom.Workspaces.Llm.GitHubAuthTokenResolver.Resolve()),
            refresh: _ => new ValueTask<string?>(Phantom.Workspaces.Llm.GitHubAuthTokenResolver.Resolve()));
        var httpClient = new Services.DevTunnel.AuthenticatedHttpClientFactory()
            .CreateClient(new Uri(repositorySource.Endpoint), tokenProvider);
        return new WebClientDataAccessLayer(repositorySource.Endpoint, httpClient);
    }

    private static async Task<IDataAccessLayer> CreateDevTunnelNameDataAccessLayerAsync(
        DevTunnelNameRepositorySource repositorySource)
    {
        // Discover the relay endpoint (and forwarded port) from the tunnel name, and keep it fresh:
        // on a connection DROP the reconnecting layer re-resolves the tunnel (picking up a changed
        // port) and reconnects with bounded backoff, without restarting the workspace. AUTH (a relay
        // 401) is a separate concern owned by the shared DevTunnelAuthenticationHandler (issue #1456)
        // inside each built client's HTTP pipeline: it attaches the Connect token per request and
        // re-mints/retries once on 401. The connect token is fetched automatically by the Management
        // API (Private mode) or absent (Anonymous).
        var resolver = new Services.DevTunnel.DevTunnelServiceFactory()
            .CreateEndpointResolver();
        var authenticatedHttpClientFactory = new Services.DevTunnel.AuthenticatedHttpClientFactory();

        var reconnectingDataAccessLayer = new Services.DevTunnel.ReconnectingWebDataAccessLayer(
            resolveEndpointAsync: cancellationToken => resolver.ResolveAsync(
                repositorySource.TunnelName,
                repositorySource.AccessMode,
                cancellationToken),
            buildDataAccessLayer: resolution =>
            {
                // Issue #1293: For Private access, only a Microsoft Connect-scope tunnel access
                // token is acceptable to the *.devtunnels.ms relay; a GitHub OAuth token is always
                // rejected with 401. If the Management API did not mint a Connect token, Resolve
                // throws an actionable error rather than silently sending the wrong token.
                // Anonymous access sends no tunnel-authorization header.
                var authorization = Services.DevTunnel.DevTunnelClientAuthorization.Resolve(
                    resolution,
                    repositorySource.AccessMode);

                // Wave 2 (#1456): the token-provider seam wraps the resolution's Connect token; the
                // concrete Management-API minting/refresh provider lands in Wave 3 (#1458).
                var tokenProvider = new Services.DevTunnel.DelegateDevTunnelConnectTokenProvider(
                    acquire: _ => new ValueTask<string?>(authorization.Token));
                var httpClient = authenticatedHttpClientFactory.CreateClient(resolution.BaseUri, tokenProvider);
                return new WebClientDataAccessLayer(resolution.BaseUri.ToString(), httpClient);
            },
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
        
        var mongoDbDataAccessLayer = TestMongoDbEntityDataAccessLayerFactory?.Invoke(mongoDbDatabase, repositorySource.RootCollectionName)
            ?? new MongoDbEntityDataAccessLayer(mongoDbDatabase, repositorySource.RootCollectionName);
        
        await mongoDbDataAccessLayer.EnsureIndexesAsync().ConfigureAwait(false);
        await mongoDbDataAccessLayer.MigrateAsync().ConfigureAwait(false);
        
        return mongoDbDataAccessLayer;
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
