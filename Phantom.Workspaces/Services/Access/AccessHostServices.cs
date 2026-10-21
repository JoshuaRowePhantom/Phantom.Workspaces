using Phantom.Workspaces.Llm.Auth;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Authorization;
using Phantom.Workspaces.Services.DevTunnel;
using Phantom.Workspaces.Services.Mcp;
using Phantom.Workspaces.Tools;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Services.Access;

public sealed class AccessHostServices : IAsyncDisposable
{
    private readonly TaskCompletionSource disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposalStarted;
    private int runtimePresentation;

    private AccessHostServices(
        OAuthProviderRegistry oauthProviders,
        IMcpOAuthCredentialRequestCatalog mcpOAuthCredentials)
    {
        this.OAuthProviders = oauthProviders;
        this.McpOAuthCredentials = mcpOAuthCredentials;
        this.GetPresentationScope = () => this.PresentationScope;
    }

    internal OAuthProviderRegistry OAuthProviders { get; }
    internal IMcpOAuthCredentialRequestCatalog McpOAuthCredentials { get; }
    public Func<AccessPresentationScope> GetPresentationScope { get; }
    internal IRemoteCopilotProviderCatalog RemoteCopilotProviders { get; } =
        new EnvironmentRemoteCopilotProviderCatalog();
    public required IAccessBroker Broker { get; init; }
    public required IUserAuthorizationRegistry AuthorizationRegistry { get; init; }
    public required AuthorizationRequestRowsViewModel AuthorizationRows { get; init; }
    public required IReleaseMemoryAdministration ReleaseMemoryAdministration { get; init; }
    public required CredentialCacheAdministration CredentialCacheAdministration { get; init; }
    public required ProviderGrantAdministration ProviderGrantAdministration { get; init; }
    public AccessPresentationScope PresentationScope => Volatile.Read(ref this.runtimePresentation) == 0
        ? AccessPresentationScope.Bootstrap : AccessPresentationScope.Runtime;

    internal DevTunnelIdentityRoute? CreateDevTunnelIdentityRoute(RepositorySource source, CredentialUseKind useKind)
    {
        RemoteAuthentication? authentication;
        DevTunnelAccessDescriptor tunnel;
        switch (source)
        {
            case WebRepositorySource web when EntityRepository.RequiresTunnelAuthentication(web):
                authentication = web.Authentication;
                tunnel = new DevTunnelAccessDescriptor
                {
                    Access = DevTunnelAccessKind.Private,
                    Endpoint = new Uri(web.Endpoint, UriKind.Absolute),
                };
                break;
            case DevTunnelNameRepositorySource named when !EntityRepository.IsAnonymous(named):
                authentication = named.Authentication;
                tunnel = new DevTunnelAccessDescriptor { Access = DevTunnelAccessKind.Private, Name = named.TunnelName };
                break;
            default:
                return null;
        }

        var purpose = useKind switch
        {
            CredentialUseKind.Repository => "repository",
            CredentialUseKind.Application => "agent-persistence",
            _ => throw new ArgumentOutOfRangeException(nameof(useKind)),
        };
        var (requestor, use) = EntityRepository.CreateConnectUse(tunnel, useKind, purpose);
        return this.CreateDevTunnelIdentityRoute(authentication, requestor, use);
    }

    internal DevTunnelIdentityRoute CreateDevTunnelIdentityRoute(
        RemoteAuthentication? authentication,
        AccessRequestor requestor,
        CredentialUseDescriptor use)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposalStarted) != 0, this);
        return new DevTunnelIdentityRouteFactory(this.OAuthProviders).Create(
            authentication, requestor, use, this.PresentationScope);
    }

    internal IDevTunnelHostService CreateDevTunnelHostService(
        RemoteAuthentication? authentication,
        IAccessBroker broker,
        Func<AccessPresentationScope> presentationScope)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposalStarted) != 0, this);
        if (!ReferenceEquals(this.Broker, broker))
        {
            throw new ArgumentException("The host service must use its owning access broker.", nameof(broker));
        }
        ArgumentNullException.ThrowIfNull(presentationScope);
        var requestor = new AccessRequestor
        {
            Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(
                AccessRequestorKind.Application, "phantom-workspaces-devtunnel-host"),
            Kind = AccessRequestorKind.Application,
            SafeDisplayName = "Workspace tunnel host",
        };
        var lineage = new CredentialUseLineage();
        var use = new CredentialUseDescriptor
        {
            Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(
                CredentialUseKind.Tunnel, lineage, requestor.Identifier, "host-tunnel-management"),
            Kind = CredentialUseKind.Tunnel,
            Lineage = lineage,
            SafeDisplayName = "Manage workspace tunnel hosting",
            MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce, ReleaseMemoryScopeKind.ExactUse, ReleaseMemoryScopeKind.AllUses],
        };
        var scope = presentationScope();
        var route = new DevTunnelIdentityRouteFactory(this.OAuthProviders).Create(
            authentication, requestor, use, scope);
        var request = DevTunnelIdentityCredentialAccessRequest.Create(route, requestor, use, scope);
        return new DevTunnelServiceFactory(broker, request, presentationScope).CreateHostService();
    }

    public void EnterRuntime()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposalStarted) != 0, this);
        Volatile.Write(ref this.runtimePresentation, 1);
        this.AuthorizationRows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
    }

    public static AccessHostServices Create(AccessHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.PlatformSecretStore);
        ArgumentNullException.ThrowIfNull(options.Dispatch);
        var policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(options.ReleaseMemoryFilePath));
        var registry = new UserAuthorizationRegistry();
        var coordinator = new ProviderAcquisitionCoordinator();
        var executors = new CredentialAccessExecutorRegistry();
        var storedSecret = new StoredSecretAccessExecutor(options.PlatformSecretStore);
        var gitHub = new GitHubCredentialAccessExecutor();
        var entra = new EntraCredentialAccessExecutor();
        var callbackBindings = new OAuthCallbackBindingFactory();
        var browserLauncher = new SystemBrowserLauncher();
        var oauthProviders = new OAuthProviderRegistry(options.PlatformSecretStore, callbackBindings);
        var oauth = new OAuthCredentialAccessExecutor(oauthProviders, browserLauncher);
        var mcpCatalog = McpOAuthComposition.CreateCatalog(options.PlatformSecretStore);
        var mcp = McpOAuthComposition.RegisterExecutor(executors, mcpCatalog, browserLauncher);
        var connect = new DevTunnelConnectCredentialAccessExecutor(
            new DevTunnelConnectMintSession(gitHub, entra, oauth, new DevTunnelConnectManagementApi()), registry);
        executors.Add<StoredSecretAccessRequest, StoredSecretAccessResult>(storedSecret);
        executors.Add<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(gitHub);
        executors.Add<EntraCredentialAccessRequest, EntraCredentialAccessResult>(entra);
        executors.Add<OAuthCredentialAccessRequest, OAuthCredentialAccessResult>(oauth);
        executors.Add<DevTunnelIdentityCredentialAccessRequest, DevTunnelIdentityCredentialAccessResult>(
            new DevTunnelIdentityCredentialAccessExecutor(gitHub, entra, oauth));
        executors.Add<DevTunnelConnectCredentialAccessRequest, DevTunnelConnectCredentialAccessResult>(connect);
        executors.AddCompletion<CliOwnedAuthorizationRequest, AuthorizationCompletedAccessResult>(
            new VsCodeTunnelCliAuthorizationExecutor());
        var broker = new AccessBroker(policy, executors, coordinator, registry);
        try
        {
            var rows = new AuthorizationRequestRowsViewModel { Dispatch = options.Dispatch, Registry = registry };
            rows.SetPresentationMode(AuthorizationRowsPresentationMode.Bootstrap);
            return new AccessHostServices(oauthProviders, mcpCatalog)
            {
                Broker = broker,
                AuthorizationRegistry = registry,
                AuthorizationRows = rows,
                ReleaseMemoryAdministration = new ReleaseMemoryAdministration { Policy = policy },
                CredentialCacheAdministration = new CredentialCacheAdministration(
                    new object[] { storedSecret, gitHub, entra, oauth, mcp, connect }.OfType<ICredentialCacheAdministration>()),
                ProviderGrantAdministration = new ProviderGrantAdministration(
                    new object[] { storedSecret, gitHub, entra, oauth, mcp, connect }.OfType<IProviderGrantAdministration>()),
            };
        }
        catch (Exception constructionFailure)
        {
            try
            {
                broker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("Access host construction and cleanup failed.", constructionFailure, cleanupFailure);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposalStarted, 1) != 0)
        {
            await this.disposal.Task.ConfigureAwait(false);
            return;
        }

        var failures = new List<Exception>();
        try
        {
            this.AuthorizationRows.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await this.CredentialCacheAdministration.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await this.ProviderGrantAdministration.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await this.Broker.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count > 0)
        {
            var failure = new AggregateException("Access host shutdown did not complete cleanly.", failures);
            this.disposal.TrySetException(failure);
            throw failure;
        }

        this.disposal.TrySetResult();
    }
}
