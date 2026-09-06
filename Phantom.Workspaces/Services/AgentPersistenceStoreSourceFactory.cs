using System;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Services;

/// <summary>
/// Builds the concrete <see cref="IAgentPersistenceStore"/> for a <see cref="RepositorySource"/>.
/// Extracted from the GUI <c>AgentSessionShortcutContext</c> (issue #1403) so the shortcut context
/// calls one factory method rather than owning the <see cref="RepositorySource"/> switch and its
/// Web / DevTunnel (reconnecting) / MongoDB / in-memory helpers. Behavior — including GitHub
/// auth-token resolution, DevTunnel reconnection, and chat-history-provider wiring — is preserved
/// exactly from the previous inline implementation.
/// </summary>
public static class AgentPersistenceStoreSourceFactory
{
    private const string AgentSessionCollectionSuffix = "-agent-sessions";

    /// <summary>
    /// Creates the persistence store for the supplied repository source. Web / DevTunnel / MongoDB
    /// sources build their respective stores; any other source resolves to an in-memory store.
    /// </summary>
    public static async Task<IAgentPersistenceStore> CreateForRepositorySourceAsync(
        RepositorySource repositorySource)
    {
        return repositorySource switch
        {
            WebRepositorySource webSource => CreateWeb(webSource),
            DevTunnelNameRepositorySource devTunnelSource => await CreateDevTunnelAsync(devTunnelSource).ConfigureAwait(false),
            MongoDbRepositorySource mongoSource => await CreateMongoDbAsync(mongoSource).ConfigureAwait(false),
            _ => AgentPersistenceStoreFactory.CreateInMemory(),
        };
    }

    private static IAgentPersistenceStore CreateWeb(WebRepositorySource repositorySource)
    {
        if (string.IsNullOrWhiteSpace(repositorySource.Endpoint))
        {
            throw new InvalidOperationException("Web repository source requires an endpoint URL.");
        }

        // The shared DevTunnelAuthenticationHandler (issue #1456) attaches/refreshes the GitHub
        // token per request; this store no longer knows about tunnels or tokens.
        if (!repositorySource.UseGitHubAuthToken)
        {
            return new Data.Web.Client.WebClientAgentPersistenceStore(repositorySource.Endpoint);
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
        return new Data.Web.Client.WebClientAgentPersistenceStore(repositorySource.Endpoint, httpClient);
    }

    private static async Task<IAgentPersistenceStore> CreateDevTunnelAsync(
        DevTunnelNameRepositorySource repositorySource)
    {
        // The reconnecting wrapper owns endpoint-DROP re-resolution; AUTH (a relay 401) is owned by
        // the shared DevTunnelAuthenticationHandler (issue #1456) inside each built store's HTTP
        // pipeline, which attaches the Connect token per request and re-mints/retries once on 401.
        var resolver = new Services.DevTunnel.DevTunnelServiceFactory()
            .CreateEndpointResolver();
        var authenticatedHttpClientFactory = new Services.DevTunnel.AuthenticatedHttpClientFactory();

        var reconnectingStore = new Services.DevTunnel.ReconnectingWebAgentPersistenceStore(
            resolveEndpointAsync: cancellationToken => resolver.ResolveAsync(
                repositorySource.TunnelName,
                repositorySource.AccessMode,
                cancellationToken),
            buildAgentPersistenceStore: resolution =>
            {
                var authorization = Services.DevTunnel.DevTunnelClientAuthorization.Resolve(
                    resolution,
                    repositorySource.AccessMode);

                // Wave 2 (#1456): the token-provider seam wraps the resolution's Connect token; the
                // concrete Management-API minting/refresh provider lands in Wave 3 (#1458).
                var tokenProvider = new Services.DevTunnel.DelegateDevTunnelConnectTokenProvider(
                    acquire: _ => new ValueTask<string?>(authorization.Token));
                var httpClient = authenticatedHttpClientFactory.CreateClient(resolution.BaseUri, tokenProvider);
                return new Data.Web.Client.WebClientAgentPersistenceStore(
                    resolution.BaseUri.ToString(),
                    httpClient);
            },
            delayScheduler: Services.DevTunnel.RealDelayScheduler.Instance);

        await reconnectingStore.StartAsync().ConfigureAwait(false);
        return reconnectingStore;
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
