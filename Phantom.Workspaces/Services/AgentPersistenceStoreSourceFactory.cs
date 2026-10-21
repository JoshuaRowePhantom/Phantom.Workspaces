using System;
using System.Threading.Tasks;
using System.Threading;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Builds the concrete <see cref="IAgentPersistenceStore"/> for a <see cref="RepositorySource"/>.
/// Extracted from the GUI <c>AgentSessionShortcutContext</c> (issue #1403) so the shortcut context
/// calls one factory method rather than owning the <see cref="RepositorySource"/> switch and its
/// Web / DevTunnel (reconnecting) / MongoDB / in-memory helpers. Behavior — including GitHub
/// auth-token resolution, DevTunnel reconnection, and chat-history-provider wiring — is preserved
/// exactly from the previous inline implementation.
/// </summary>
public sealed class AgentPersistenceStoreSourceFactory
{
    private const string AgentSessionCollectionSuffix = "-agent-sessions";
    private readonly RepositorySource repositorySource;
    private readonly IAccessBroker? accessBroker;
    private readonly DevTunnelIdentityRoute? identityRoute;
    private readonly Func<AccessPresentationScope>? presentationScope;
    private readonly IAuthenticatedHttpClientFactory httpClientFactory;
    private readonly Func<System.Net.Http.HttpMessageHandler>? managementTransportFactory;

    public AgentPersistenceStoreSourceFactory(
        RepositorySource repositorySource,
        IAccessBroker? accessBroker = null,
        DevTunnelIdentityRoute? identityRoute = null,
        Func<AccessPresentationScope>? presentationScope = null)
        : this(repositorySource, accessBroker, identityRoute, presentationScope, new AuthenticatedHttpClientFactory())
    {
    }

    internal AgentPersistenceStoreSourceFactory(
        RepositorySource repositorySource,
        IAccessBroker? accessBroker,
        DevTunnelIdentityRoute? identityRoute,
        Func<AccessPresentationScope>? presentationScope,
        IAuthenticatedHttpClientFactory httpClientFactory,
        Func<System.Net.Http.HttpMessageHandler>? managementTransportFactory = null)
    {
        this.repositorySource = repositorySource;
        this.accessBroker = accessBroker;
        this.identityRoute = identityRoute;
        this.presentationScope = presentationScope;
        this.httpClientFactory = httpClientFactory;
        this.managementTransportFactory = managementTransportFactory;
    }

    /// <summary>
    /// Creates the persistence store for the supplied repository source. Web / DevTunnel / MongoDB
    /// sources build their respective stores; any other source resolves to an in-memory store.
    /// </summary>
    public async Task<IAgentPersistenceStore> CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return this.repositorySource switch
        {
            WebRepositorySource webSource => this.CreateWeb(webSource),
            DevTunnelNameRepositorySource devTunnelSource => await this.CreateDevTunnelAsync(devTunnelSource, cancellationToken).ConfigureAwait(false),
            MongoDbRepositorySource mongoSource => await CreateMongoDbAsync(mongoSource).ConfigureAwait(false),
            _ => AgentPersistenceStoreFactory.CreateInMemory(),
        };
    }

    private IAgentPersistenceStore CreateWeb(WebRepositorySource repositorySource)
    {
        if (string.IsNullOrWhiteSpace(repositorySource.Endpoint))
        {
            throw new InvalidOperationException("Web repository source requires an endpoint URL.");
        }

        if (!EntityRepository.RequiresTunnelAuthentication(repositorySource))
        {
            return new Data.Web.Client.WebClientAgentPersistenceStore(repositorySource.Endpoint);
        }

        var broker = EntityRepository.RequireAccessBroker(this.accessBroker);
        var scope = EntityRepository.RequirePresentationScope(this.presentationScope);
        var endpoint = new Uri(repositorySource.Endpoint, UriKind.Absolute);
        var request = EntityRepository.CreateConnectRequest(
            new DevTunnelAccessDescriptor { Access = DevTunnelAccessKind.Private, Endpoint = endpoint },
            repositorySource.Authentication, this.identityRoute, CredentialUseKind.Application, "agent-persistence", scope);
        var httpClient = this.httpClientFactory.CreateClient(endpoint, broker, request, scope);
        return new Data.Web.Client.WebClientAgentPersistenceStore(repositorySource.Endpoint, httpClient, ownsHttpClient: true);
    }

    private async Task<IAgentPersistenceStore> CreateDevTunnelAsync(
        DevTunnelNameRepositorySource repositorySource,
        CancellationToken cancellationToken)
    {
        var broker = EntityRepository.RequireAccessBroker(this.accessBroker);
        var scope = EntityRepository.RequirePresentationScope(this.presentationScope);
        var anonymous = EntityRepository.IsAnonymous(repositorySource);
        var request = EntityRepository.CreateConnectRequest(
            new DevTunnelAccessDescriptor
            {
                Access = anonymous ? DevTunnelAccessKind.Anonymous : DevTunnelAccessKind.Private,
                Name = repositorySource.TunnelName,
            },
            repositorySource.Authentication, this.identityRoute, CredentialUseKind.Application, "agent-persistence", scope);
        var identity = anonymous
            ? EntityRepository.CreateNamedManagementIdentityRequest(
                repositorySource, this.identityRoute, CredentialUseKind.Application, "agent-persistence", scope)
            : DevTunnelIdentityCredentialAccessRequest.Create(
                request.IdentityRoute, request.Requestor, request.Use, request.PresentationScope);
        var resolver = new DevTunnelServiceFactory(
            broker, identity, scope)
        {
            ManagementTransportFactory = this.managementTransportFactory,
        }.CreateEndpointResolver(request);

        var reconnectingStore = new Services.DevTunnel.ReconnectingWebAgentPersistenceStore(
            resolveEndpointAsync: cancellationToken => resolver.ResolveAsync(
                repositorySource.TunnelName,
                anonymous ? Configuration.DevTunnelAccessMode.Anonymous : Configuration.DevTunnelAccessMode.Private,
                cancellationToken),
            buildAgentPersistenceStore: resolution =>
            {
                var relayRequest = EntityRepository.BindAnonymousEndpoint(request, resolution.BaseUri, scope());
                var httpClient = this.httpClientFactory.CreateClient(resolution.BaseUri, broker, relayRequest, scope);
                return new Data.Web.Client.WebClientAgentPersistenceStore(
                    resolution.BaseUri.ToString(),
                    httpClient,
                    ownsHttpClient: true);
            },
            delayScheduler: Services.DevTunnel.RealDelayScheduler.Instance);

        var initialized = false;
        try
        {
            await reconnectingStore.StartAsync(cancellationToken).ConfigureAwait(false);
            initialized = true;
            return reconnectingStore;
        }
        finally
        {
            if (!initialized)
            {
                await reconnectingStore.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<IAgentPersistenceStore> CreateMongoDbAsync(
        MongoDbRepositorySource mongoSource)
    {
        if (string.IsNullOrWhiteSpace(mongoSource.ContainerName)
            || string.IsNullOrWhiteSpace(mongoSource.RootCollectionName))
        {
            return AgentPersistenceStoreFactory.CreateInMemory();
        }

        var mongoDbDataDirectory = mongoSource.DataDirectory ?? string.Empty;
        var mongoDbDatabaseName = string.IsNullOrWhiteSpace(mongoSource.DatabaseName)
            ? "phantom-workspaces"
            : mongoSource.DatabaseName;
        var agentSessionCollectionName = $"{mongoSource.RootCollectionName}{AgentSessionCollectionSuffix}";
        var chatHistoryProviderDefinition = ChatHistoryProviderDefinition.CreateMongoDb(
            provider: "container",
            databaseName: mongoDbDatabaseName,
            collectionName: agentSessionCollectionName,
            containerName: mongoSource.ContainerName,
            dataDirectory: mongoDbDataDirectory,
            hostPort: mongoSource.HostPort);
        return await AgentPersistenceStoreFactory.CreateAsync(chatHistoryProviderDefinition);
    }
}
