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
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces;

public sealed class EntityRepository : IAsyncDisposable
{
    private readonly IDataAccessLayer coreDataAccessLayer;
    private readonly IDataAccessLayer underlyingDataAccessLayer;
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposalStarted;

    internal static Func<MongoDB.Driver.IMongoDatabase, string, MongoDbEntityDataAccessLayer>? TestMongoDbEntityDataAccessLayerFactory { get; set; }

    private EntityRepository(
        RepositorySource repositorySource,
        IDataAccessLayer coreDataAccessLayer,
        WorkspaceEntitySession workspaceEntitySession,
        IAccessBroker? accessBroker,
        DevTunnelIdentityRoute? identityRoute,
        Func<AccessPresentationScope>? presentationScope,
        IDataAccessLayer underlyingDataAccessLayer)
    {
        this.RepositorySource = repositorySource;
        this.AccessBroker = accessBroker;
        this.DevTunnelIdentityRoute = identityRoute;
        this.PresentationScope = presentationScope;
        this.underlyingDataAccessLayer = underlyingDataAccessLayer;
        this.coreDataAccessLayer = coreDataAccessLayer;
        this.WorkspaceEntitySession = workspaceEntitySession;
        this.DataAccessLayer = new WorkspaceEntitySessionDataAccessLayer(this.coreDataAccessLayer, this.WorkspaceEntitySession);
    }

    public RepositorySource RepositorySource { get; }

    public IAccessBroker? AccessBroker { get; }

    public DevTunnelIdentityRoute? DevTunnelIdentityRoute { get; }

    public Func<AccessPresentationScope>? PresentationScope { get; }

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
        return await CreateAsync(
            new EntityRepositoryInitializationOptions
            {
                RepositorySource = repositorySource,
                UserComputerProfileOverride = userComputerProfileOverride,
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    internal static async Task<EntityRepository> CreateAsync(
        EntityRepositoryInitializationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        using var startup = RepositoryStartupDiagnostics.Start("repository-initialization");
        var repositorySource = options.RepositorySource;
        IDataAccessLayer underlyingDataAccessLayer;
        using (RepositoryStartupDiagnostics.Start("repository-underlying-layer"))
            underlyingDataAccessLayer = await CreateUnderlyingDataAccessLayerAsync(
                repositorySource, options.AccessBroker, options.DevTunnelIdentityRoute, options.AccessPresentationScope, cancellationToken).ConfigureAwait(false);
        try
        {
            var isWebSource = repositorySource is WebRepositorySource or DevTunnelNameRepositorySource;
            IDataAccessLayer innerDataAccessLayer;
            if (isWebSource)
            {
                innerDataAccessLayer = underlyingDataAccessLayer;
            }
            else
            {
                innerDataAccessLayer = ValidatedDataAccessLayerFactory.Create(
                    underlyingDataAccessLayer,
                    options.ValidationPassStarted);
            }
            if (!isWebSource)
            {
                // For MongoDB data access layers, ensure indexes and migrate schema before any reads
                if (underlyingDataAccessLayer is MongoDbEntityDataAccessLayer mongoDbDataAccessLayer)
                {
                    await mongoDbDataAccessLayer.EnsureIndexesAsync().ConfigureAwait(false);
                    await mongoDbDataAccessLayer.MigrateAsync().ConfigureAwait(false);
                }

                using (RepositoryStartupDiagnostics.Start("repository-schema-population"))
                    await EnsureSeedDataIfNeededAsync(innerDataAccessLayer, cancellationToken).ConfigureAwait(false);
            }

            var coreDataAccessLayer = new ScheduleDataAccessLayer(innerDataAccessLayer);
            WorkspaceEntitySession workspaceEntitySession;
            using (RepositoryStartupDiagnostics.Start("repository-session-bootstrap"))
                workspaceEntitySession = await WorkspaceEntitySessionBootstrapper.InitializeAsync(
                    coreDataAccessLayer,
                    options.UserComputerProfileOverride,
                    cancellationToken).ConfigureAwait(false);
            return new EntityRepository(
                repositorySource, coreDataAccessLayer, workspaceEntitySession,
                options.AccessBroker, options.DevTunnelIdentityRoute, options.AccessPresentationScope,
                underlyingDataAccessLayer);
        }
        catch (Exception initializationFailure)
        {
            try
            {
                await DisposeLayerAsync(underlyingDataAccessLayer).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Repository initialization and cleanup failed.", initializationFailure, cleanupFailure);
            }
            throw;
        }
    }

    private static async Task<IDataAccessLayer> CreateUnderlyingDataAccessLayerAsync(
        RepositorySource repositorySource,
        IAccessBroker? accessBroker,
        DevTunnelIdentityRoute? identityRoute,
        Func<AccessPresentationScope>? presentationScope,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return repositorySource switch
        {
            WebRepositorySource web => CreateWebDataAccessLayer(web, accessBroker, identityRoute, presentationScope),
            DevTunnelNameRepositorySource devTunnel => await CreateDevTunnelNameDataAccessLayerAsync(
                devTunnel, accessBroker, identityRoute, presentationScope, cancellationToken).ConfigureAwait(false),
            LocalGitRepositorySource git => new GitDataAccessLayer(git.Path),
            MongoDbRepositorySource mongo => await CreateMongoDbDataAccessLayerAsync(mongo).ConfigureAwait(false),
            _ => new InMemoryDataAccessLayer(),
        };
    }

    internal static IDataAccessLayer CreateWebDataAccessLayer(
        WebRepositorySource repositorySource,
        IAccessBroker? accessBroker,
        DevTunnelIdentityRoute? identityRoute,
        Func<AccessPresentationScope>? presentationScope,
        IAuthenticatedHttpClientFactory? httpClientFactory = null)
    {
        if (string.IsNullOrWhiteSpace(repositorySource.Endpoint))
        {
            throw new InvalidOperationException("Web repository source requires an endpoint URL.");
        }

        if (!RequiresTunnelAuthentication(repositorySource))
        {
            return new WebClientDataAccessLayer(repositorySource.Endpoint);
        }

        var broker = RequireAccessBroker(accessBroker);
        var scope = RequirePresentationScope(presentationScope);
        var endpoint = new Uri(repositorySource.Endpoint, UriKind.Absolute);
        var request = CreateConnectRequest(
            new DevTunnelAccessDescriptor { Access = DevTunnelAccessKind.Private, Endpoint = endpoint },
            repositorySource.Authentication, identityRoute, CredentialUseKind.Repository, "repository", scope);
        var httpClient = (httpClientFactory ?? new AuthenticatedHttpClientFactory())
            .CreateClient(endpoint, broker, request, scope);
        return new WebClientDataAccessLayer(repositorySource.Endpoint, httpClient, ownsHttpClient: true);
    }

    internal static async Task<IDataAccessLayer> CreateDevTunnelNameDataAccessLayerAsync(
        DevTunnelNameRepositorySource repositorySource,
        IAccessBroker? accessBroker,
        DevTunnelIdentityRoute? identityRoute,
        Func<AccessPresentationScope>? presentationScope,
        CancellationToken cancellationToken,
        IAuthenticatedHttpClientFactory? httpClientFactory = null)
    {
        var broker = RequireAccessBroker(accessBroker);
        var scope = RequirePresentationScope(presentationScope);
        var anonymous = IsAnonymous(repositorySource);
        var request = CreateConnectRequest(
            new DevTunnelAccessDescriptor
            {
                Access = anonymous ? DevTunnelAccessKind.Anonymous : DevTunnelAccessKind.Private,
                Name = repositorySource.TunnelName,
            },
            repositorySource.Authentication, identityRoute, CredentialUseKind.Repository, "repository", scope);
        var identity = DevTunnelIdentityCredentialAccessRequest.Create(
            request.IdentityRoute, request.Requestor, request.Use, request.PresentationScope);
        var resolver = new DevTunnelServiceFactory(
            broker, identity, scope).CreateEndpointResolver(request);
        var authenticatedHttpClientFactory = httpClientFactory ?? new AuthenticatedHttpClientFactory();

        var reconnectingDataAccessLayer = new Services.DevTunnel.ReconnectingWebDataAccessLayer(
            resolveEndpointAsync: cancellationToken => resolver.ResolveAsync(
                repositorySource.TunnelName,
                anonymous ? Configuration.DevTunnelAccessMode.Anonymous : Configuration.DevTunnelAccessMode.Private,
                cancellationToken),
            buildDataAccessLayer: resolution =>
            {
                var httpClient = authenticatedHttpClientFactory.CreateClient(resolution.BaseUri, broker, request, scope);
                return new WebClientDataAccessLayer(resolution.BaseUri.ToString(), httpClient, ownsHttpClient: true);
            },
            delayScheduler: Services.DevTunnel.RealDelayScheduler.Instance);

        var initialized = false;
        try
        {
            await reconnectingDataAccessLayer.StartAsync(cancellationToken).ConfigureAwait(false);
            initialized = true;
            return reconnectingDataAccessLayer;
        }
        finally
        {
            if (!initialized)
            {
                await reconnectingDataAccessLayer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static bool RequiresTunnelAuthentication(WebRepositorySource source) =>
        source.Authentication is { } authentication
            ? !string.Equals(authentication.Scheme, RemoteAuthentication.AnonymousScheme, StringComparison.OrdinalIgnoreCase)
            : source.UseGitHubAuthToken;

    internal static bool IsAnonymous(DevTunnelNameRepositorySource source) =>
        source.AccessMode == Configuration.DevTunnelAccessMode.Anonymous ||
        string.Equals(source.Authentication?.Scheme, RemoteAuthentication.AnonymousScheme, StringComparison.OrdinalIgnoreCase);

    internal static IAccessBroker RequireAccessBroker(IAccessBroker? broker) =>
        broker ?? throw new InvalidOperationException("The host access broker is required for relay access.");

    internal static Func<AccessPresentationScope> RequirePresentationScope(Func<AccessPresentationScope>? presentationScope) =>
        presentationScope ?? throw new DevTunnelCredentialAccessException(AccessErrorCode.UnsupportedRequest);

    internal static DevTunnelConnectCredentialAccessRequest CreateConnectRequest(
        DevTunnelAccessDescriptor tunnel,
        RemoteAuthentication? authentication,
        DevTunnelIdentityRoute? configuredRoute,
        CredentialUseKind useKind,
        string purpose,
        Func<AccessPresentationScope> presentationScope)
    {
        if (tunnel.Endpoint is { } endpoint &&
            (endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0))
        {
            throw new InvalidOperationException("Relay endpoints cannot contain credential-bearing URL components.");
        }
        var requestor = new AccessRequestor
        {
            Kind = AccessRequestorKind.Application,
            Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(AccessRequestorKind.Application, purpose),
            SafeDisplayName = useKind == CredentialUseKind.Repository ? "Repository" : "Agent persistence",
        };
        var resource = tunnel.Name ?? tunnel.Endpoint?.AbsoluteUri ?? string.Empty;
        var lineage = new CredentialUseLineage
        {
            ProviderConfiguration = CredentialIdentityFactory.CreateLineageIdentifier("relay-resource", resource),
        };
        var use = new CredentialUseDescriptor
        {
            Kind = useKind,
            Lineage = lineage,
            Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(useKind, lineage, requestor.Identifier, purpose),
            SafeDisplayName = useKind == CredentialUseKind.Repository ? "Access repository relay" : "Access persistence relay",
            MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce, ReleaseMemoryScopeKind.ExactUse, ReleaseMemoryScopeKind.AllUses],
        };
        var scope = RequirePresentationScope(presentationScope)();
        DevTunnelIdentityRoute route;
        if (tunnel.Access == DevTunnelAccessKind.Anonymous)
        {
            route = new AnonymousDevTunnelIdentityRoute();
        }
        else if (configuredRoute is not null)
        {
            if (configuredRoute is AnonymousDevTunnelIdentityRoute ||
                authentication is not null && !string.Equals(authentication.Scheme, configuredRoute.Scheme.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The configured identity route does not match relay authentication.");
            }
            route = configuredRoute;
        }
        else if (authentication is null || string.Equals(authentication.Scheme, RemoteAuthentication.GithubScheme, StringComparison.OrdinalIgnoreCase))
        {
            route = new GitHubDevTunnelIdentityRoute
            {
                Credential = GitHubCredentialAccessRequest.Create(
                    GitHubCredentialPurpose.DevTunnelIdentity, requestor, lineage, purpose, scope),
            };
        }
        else
        {
            throw new InvalidOperationException("A configured typed identity route is required for this relay authentication scheme.");
        }
        return DevTunnelConnectCredentialAccessRequest.Create(tunnel, route, requestor, use, scope);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposalStarted, 1) != 0)
        {
            await this.disposal.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            await DisposeLayerAsync(this.underlyingDataAccessLayer).ConfigureAwait(false);
            this.disposal.TrySetResult();
        }
        catch (Exception exception)
        {
            this.disposal.TrySetException(exception);
            throw;
        }
    }

    private static async ValueTask DisposeLayerAsync(IDataAccessLayer layer)
    {
        if (layer is IAsyncDisposable asynchronous) { await asynchronous.DisposeAsync().ConfigureAwait(false); }
        else if (layer is IDisposable synchronous) { synchronous.Dispose(); }
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
        IDataAccessLayer dataAccessLayer,
        CancellationToken cancellationToken)
    {
        var errors = await new SchemaPopulator(dataAccessLayer).PopulateAsync(cancellationToken);
        if (errors.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Failed to populate repository schemas: {string.Join(" | ", errors.Select(static error => error.Message))}");
    }
}
