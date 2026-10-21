using System.IO;
using System.Security;
using ModelContextProtocol.Authentication;
using Moq;
using AgentSchema;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.DevTunnel;
using Phantom.Workspaces.Services.Mcp;
using Phantom.Workspaces.Llm.Interfaces.Authorization;

namespace Phantom.Workspaces.Tests.Authorization;

public sealed class HostConfiguredAccessGraphTests
{
    [Fact]
    public async Task ActualHostAggregateAdministersLateRegisteredMcpCacheWithoutChangingAuthorization()
    {
        using var store = new CachedPlatformStore();
        await using var host = AccessHostServices.Create(new AccessHostOptions
        {
            PlatformSecretStore = store,
            Dispatch = action => action(),
            ReleaseMemoryFilePath = Path.Combine(Environment.CurrentDirectory, ".nonexistent-host-mcp-administration",
                Guid.NewGuid().ToString("N"), "release-memory.json"),
        });
        var (requestor, use) = CreateUse();
        var request = Assert.IsType<McpOAuthCredentialUseRequest>(host.McpOAuthCredentials.CreateRequest(
            new McpTool
            {
                ServerName = "late-root-cache",
                Connection = new OAuthConnection
                {
                    Endpoint = "https://mcp.invalid/",
                    ClientId = "root-public-client",
                    Scopes = ["read"],
                },
            }, requestor, use, host.GetPresentationScope()));
        var configuration = Assert.IsType<McpOAuthCredentialRequestCatalog>(host.McpOAuthCredentials)
            .FindConfiguration(request.ProviderConfiguration);
        Assert.NotNull(configuration);
        var generation = new CredentialMaterialGenerationIdentifier { Value = Guid.NewGuid() };
        await configuration.Cache.ReserveGenerationAsync(generation, TestContext.Current.CancellationToken);
        await configuration.Cache.StoreGenerationAsync(new TokenContainer
        {
            AccessToken = "test-cache-material",
            TokenType = "Bearer",
            ObtainedAt = DateTimeOffset.UtcNow,
            ExpiresIn = 3600,
        }, generation, TestContext.Current.CancellationToken);
        var reads = store.Reads;

        var descriptor = Assert.Single(await host.CredentialCacheAdministration.ListAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(CredentialProviderKind.McpOAuth, descriptor.Provider);
        Assert.Equal(generation, descriptor.Generation);
        Assert.Equal(reads, store.Reads);
        await host.CredentialCacheAdministration.DeleteAsync(descriptor.Identifier, TestContext.Current.CancellationToken);

        Assert.Equal(1, store.Deletes);
        Assert.Null(await ((ICredentialGenerationCache)configuration.Cache)
            .InspectGenerationAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await host.CredentialCacheAdministration.ListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(host.AuthorizationRegistry.GetSnapshot());
        Assert.Empty(await host.ReleaseMemoryAdministration.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ActualHostMcpCatalogRegistersFirstUseWithoutMaterialOrProviderWork()
    {
        var store = new Mock<IPlatformSecretStore>(MockBehavior.Strict);
        await using var host = AccessHostServices.Create(new AccessHostOptions
        {
            PlatformSecretStore = store.Object,
            Dispatch = action => action(),
            ReleaseMemoryFilePath = Path.Combine(Environment.CurrentDirectory, ".nonexistent-host-mcp-access",
                Guid.NewGuid().ToString("N"), "release-memory.json"),
        });
        var (requestor, use) = CreateUse();
        var tool = new McpTool
        {
            ServerName = "actual-root-mcp",
            Connection = new OAuthConnection
            {
                Endpoint = "https://mcp.invalid/",
                ClientId = "root-public-client",
                Scopes = ["read"],
            },
        };
        var request = Assert.IsType<McpOAuthCredentialUseRequest>(
            host.McpOAuthCredentials.CreateRequest(tool, requestor, use, host.GetPresentationScope()));
        var pending = host.Broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        var row = Assert.Single(host.AuthorizationRegistry.GetSnapshot());
        Assert.Equal(row.Identifier, Assert.Single(host.AuthorizationRows.Rows).Identifier);
        Assert.True(row.IsBootstrapBlocking);
        Assert.False(pending.IsCompleted);
        store.VerifyNoOtherCalls();

        await host.AuthorizationRegistry.ActAsync(new DenyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
        }, TestContext.Current.CancellationToken);
        Assert.IsType<AccessDenied<McpCredentialUseResult>>(await pending);

        host.EnterRuntime();
        var runtime = Assert.IsType<McpOAuthCredentialUseRequest>(
            host.McpOAuthCredentials.CreateRequest(tool, requestor, use, host.GetPresentationScope()));
        Assert.Equal(request.ProviderConfiguration, runtime.ProviderConfiguration);
        Assert.Equal(AccessPresentationScope.Runtime, runtime.PresentationScope);
        store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("https://transport.invalid/connect")]
    [InlineData(null)]
    public async Task ConfiguredOAuthRoutesUseOneHostRegistryAndCurrentPresentation(string? endpoint)
    {
        await using var host = CreateHost();
        var authentication = OAuthAuthentication(endpoint);
        var (requestor, use) = CreateUse();
        var bootstrap = Assert.IsType<OAuthDevTunnelIdentityRoute>(
            host.CreateDevTunnelIdentityRoute(authentication, requestor, use));
        var configuration = host.OAuthProviders.FindConfiguration(bootstrap.Credential.Credential.LogicalIdentifier);
        Assert.NotNull(configuration);
        Assert.Equal(authentication.AccountEndpoint, configuration.Provider.AccountEndpoint.AbsoluteUri);
        Assert.Equal(authentication.Audience, configuration.Provider.Audience);
        Assert.Equal(OAuthAccountIdentifierClaim.Subject, configuration.Provider.AccountIdentifierClaim);
        Assert.Equal(AccessPresentationScope.Bootstrap, bootstrap.Credential.PresentationScope);

        host.EnterRuntime();
        var runtime = Assert.IsType<OAuthDevTunnelIdentityRoute>(
            host.CreateDevTunnelIdentityRoute(authentication, requestor, use));
        Assert.Same(configuration, host.OAuthProviders.FindConfiguration(runtime.Credential.Credential.LogicalIdentifier));
        Assert.Same(requestor, runtime.Credential.Requestor);
        Assert.Same(use, runtime.Credential.Use);
        Assert.Equal(AccessPresentationScope.Runtime, runtime.Credential.PresentationScope);
        Assert.Empty(host.AuthorizationRegistry.GetSnapshot());
    }

    [Theory]
    [InlineData("https://transport.invalid/connect", CredentialUseKind.Repository)]
    [InlineData(null, CredentialUseKind.Repository)]
    [InlineData("https://transport.invalid/connect", CredentialUseKind.Application)]
    [InlineData(null, CredentialUseKind.Application)]
    public async Task ConfiguredOAuthSourceRouteUsesAudienceIndependentlyOfEndpoint(string? endpoint, CredentialUseKind useKind)
    {
        var store = new Mock<IPlatformSecretStore>(MockBehavior.Strict);
        await using var host = AccessHostServices.Create(new AccessHostOptions
        {
            PlatformSecretStore = store.Object,
            Dispatch = action => action(),
            ReleaseMemoryFilePath = Path.Combine(Environment.CurrentDirectory, ".nonexistent-host-oauth-audience",
                Guid.NewGuid().ToString("N"), "release-memory.json"),
        });
        var authentication = OAuthAuthentication(endpoint);
        var source = new DevTunnelNameRepositorySource("configured-oauth-tunnel", Configuration.DevTunnelAccessMode.Private, authentication);
        var bootstrap = Assert.IsType<OAuthDevTunnelIdentityRoute>(host.CreateDevTunnelIdentityRoute(source, useKind));
        var configuration = Assert.Single(host.OAuthProviders.SnapshotConfigurations());
        Assert.Equal(authentication.Audience, configuration.Provider.Audience);
        Assert.NotEqual(endpoint, configuration.Provider.Audience);
        Assert.Equal(useKind, bootstrap.Credential.Use.Kind);
        Assert.Equal(AccessPresentationScope.Bootstrap, bootstrap.Credential.PresentationScope);

        host.EnterRuntime();
        var runtime = Assert.IsType<OAuthDevTunnelIdentityRoute>(host.CreateDevTunnelIdentityRoute(source, useKind));
        Assert.Equal(bootstrap.Credential.Credential.LogicalIdentifier, runtime.Credential.Credential.LogicalIdentifier);
        Assert.Same(configuration, Assert.Single(host.OAuthProviders.SnapshotConfigurations()));
        Assert.Equal(AccessPresentationScope.Runtime, runtime.Credential.PresentationScope);
        Assert.Empty(host.AuthorizationRegistry.GetSnapshot());
        store.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("relative-audience")]
    [InlineData("/relative-audience")]
    public async Task InvalidConfiguredOAuthAudienceFailsInActualRootBeforeRegistration(string? audience)
    {
        var store = new Mock<IPlatformSecretStore>(MockBehavior.Strict);
        await using var host = AccessHostServices.Create(new AccessHostOptions
        {
            PlatformSecretStore = store.Object,
            Dispatch = action => action(),
            ReleaseMemoryFilePath = Path.Combine(Environment.CurrentDirectory, ".nonexistent-host-oauth-audience",
                Guid.NewGuid().ToString("N"), "release-memory.json"),
        });
        var source = new DevTunnelNameRepositorySource("configured-oauth-tunnel", Configuration.DevTunnelAccessMode.Private,
            OAuthAuthentication("https://transport.invalid/connect") with { Audience = audience });

        var failure = Assert.Throws<DevTunnelCredentialAccessException>(() =>
            host.CreateDevTunnelIdentityRoute(source, CredentialUseKind.Repository));

        Assert.Equal(AccessErrorCode.UnsupportedRequest, failure.ErrorCode);
        Assert.Empty(host.OAuthProviders.SnapshotConfigurations());
        Assert.Empty(host.AuthorizationRegistry.GetSnapshot());
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingTrustedOAuthMetadataFailsBeforeRequestRegistration()
    {
        await using var host = CreateHost();
        var (requestor, use) = CreateUse();
        Assert.Throws<DevTunnelCredentialAccessException>(() => host.CreateDevTunnelIdentityRoute(
            new RemoteAuthentication(RemoteAuthentication.OAuthScheme, ClientId: "not-enough-metadata"),
            requestor, use));
        Assert.Empty(host.AuthorizationRegistry.GetSnapshot());
    }

    [Theory]
    [InlineData(CredentialUseKind.Repository)]
    [InlineData(CredentialUseKind.Application)]
    public async Task ConfiguredSourceRouteUsesActualCallerContextAndLiveHostPhase(CredentialUseKind useKind)
    {
        await using var host = CreateHost();
        var authentication = new RemoteAuthentication(
            RemoteAuthentication.EntraScheme,
            Authority: "https://login.microsoftonline.com/organizations",
            ClientId: "configured-test-client",
            Scopes: ["https://tunnels.api.visualstudio.com/.default"]);
        var source = new DevTunnelNameRepositorySource(
            "configured-test-tunnel", Configuration.DevTunnelAccessMode.Private, authentication);
        var configured = Assert.IsType<EntraDevTunnelIdentityRoute>(
            host.CreateDevTunnelIdentityRoute(source, CredentialUseKind.Repository));
        var purpose = useKind == CredentialUseKind.Repository ? "repository" : "agent-persistence";
        var tunnel = new DevTunnelAccessDescriptor { Access = DevTunnelAccessKind.Private, Name = source.TunnelName };
        var presentation = host.GetPresentationScope;

        var bootstrap = EntityRepository.CreateConnectRequest(
            tunnel, authentication, configured, useKind, purpose, presentation);
        var bootstrapIdentity = Assert.IsType<EntraDevTunnelIdentityRoute>(bootstrap.IdentityRoute).Credential;
        Assert.Same(bootstrap.Requestor, bootstrapIdentity.Requestor);
        Assert.Same(bootstrap.Use, bootstrapIdentity.Use);
        Assert.Equal(useKind, bootstrapIdentity.Use.Kind);
        Assert.Equal(AccessPresentationScope.Bootstrap, bootstrapIdentity.PresentationScope);

        host.EnterRuntime();
        var runtime = EntityRepository.CreateConnectRequest(
            tunnel, authentication, configured, useKind, purpose, presentation);
        var runtimeIdentity = Assert.IsType<EntraDevTunnelIdentityRoute>(runtime.IdentityRoute).Credential;
        Assert.Same(presentation, host.GetPresentationScope);
        Assert.Same(runtime.Requestor, runtimeIdentity.Requestor);
        Assert.Same(runtime.Use, runtimeIdentity.Use);
        Assert.Equal(AccessPresentationScope.Runtime, runtimeIdentity.PresentationScope);
        Assert.Equal(AccessPresentationScope.Bootstrap, configured.Credential.PresentationScope);
        Assert.Equal(bootstrapIdentity.Credential.LogicalIdentifier, runtimeIdentity.Credential.LogicalIdentifier);
        Assert.Empty(host.AuthorizationRegistry.GetSnapshot());
    }

    [Fact]
    public async Task RealHostFactoryRejectsAnAlternateBrokerBeforeCreatingTransport()
    {
        await using var host = CreateHost();
        var scopeReads = 0;
        Assert.Throws<ArgumentException>(() => host.CreateDevTunnelHostService(
            null, Mock.Of<IAccessBroker>(), () =>
            {
                scopeReads++;
                return host.PresentationScope;
            }));
        Assert.Equal(0, scopeReads);
        Assert.Empty(host.AuthorizationRegistry.GetSnapshot());
    }

    private static RemoteAuthentication OAuthAuthentication(string? endpoint) => new(
        RemoteAuthentication.OAuthScheme,
        Endpoint: endpoint,
        Authority: "https://identity.invalid/",
        ClientId: "configured-host-client",
        AuthorizationEndpoint: "https://identity.invalid/authorize",
        TokenEndpoint: "https://identity.invalid/token",
        Scopes: ["tunnel", "profile"],
        AccountEndpoint: "https://identity.invalid/account",
        AccountIdentifierClaim: OAuthAccountIdentifierClaim.Subject,
        Audience: "https://tunnels.api.visualstudio.com/");

    private sealed class CachedPlatformStore : IPlatformSecretStore, IDisposable
    {
        private readonly Dictionary<string, SecureString> values = [];
        internal int Reads { get; private set; }
        internal int Deletes { get; private set; }

        public Task<SecureString?> ReadAsync(string name, CancellationToken cancellationToken)
        {
            this.Reads++;
            return Task.FromResult(this.values.TryGetValue(name, out var value) ? value.Copy() : null);
        }

        public Task WriteAsync(string name, SecureString value, CancellationToken cancellationToken)
        {
            if (this.values.Remove(name, out var previous)) { previous.Dispose(); }
            this.values.Add(name, value.Copy());
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            this.Deletes++;
            if (this.values.Remove(name, out var value)) { value.Dispose(); }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> EnumerateNamesAsync(string prefix, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(this.values.Keys.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).ToArray());

        public void Dispose()
        {
            foreach (var value in this.values.Values) { value.Dispose(); }
            this.values.Clear();
        }
    }

    private static AccessHostServices CreateHost() => AccessHostServices.Create(new AccessHostOptions
    {
        PlatformSecretStore = new NullPlatformSecretStore(),
        Dispatch = action => action(),
        ReleaseMemoryFilePath = Path.Combine(Environment.CurrentDirectory, ".nonexistent-host-configured-access",
            Guid.NewGuid().ToString("N"), "release-memory.json"),
    });

    private static (AccessRequestor, CredentialUseDescriptor) CreateUse()
    {
        var requestor = new AccessRequestor
        {
            Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(
                AccessRequestorKind.Application, "configured-host-graph-test"),
            Kind = AccessRequestorKind.Application,
            SafeDisplayName = "Configured host test",
        };
        var lineage = new CredentialUseLineage();
        return (requestor, new CredentialUseDescriptor
        {
            Fingerprint = CredentialIdentityFactory.CreateUseFingerprint(
                CredentialUseKind.Tunnel, lineage, requestor.Identifier, "configured-host-management"),
            Kind = CredentialUseKind.Tunnel,
            Lineage = lineage,
            SafeDisplayName = "Manage configured test tunnel",
            MemoryScopes = [ReleaseMemoryScopeKind.AllowOnce],
        });
    }
}
