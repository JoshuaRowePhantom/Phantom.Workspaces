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

        return new Data.Web.Client.WebClientAgentPersistenceStore(repositorySource.Endpoint, devTunnelAccessToken, devTunnelAccessTokenResolver);
    }

    private static async Task<IAgentPersistenceStore> CreateDevTunnelAsync(
        DevTunnelNameRepositorySource repositorySource)
    {
        var resolver = new Services.DevTunnel.DevTunnelServiceFactory()
            .CreateEndpointResolver();

        var reconnectingStore = new Services.DevTunnel.ReconnectingWebAgentPersistenceStore(
            resolveEndpointAsync: cancellationToken => resolver.ResolveAsync(
                repositorySource.TunnelName,
                repositorySource.AccessMode,
                cancellationToken),
            buildAgentPersistenceStore: resolution => new Data.Web.Client.WebClientAgentPersistenceStore(
                resolution.BaseUri.ToString(),
                resolution.TunnelAuthToken),
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
