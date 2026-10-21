using System.IO;
using Moq;
using AgentSchema;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.DevTunnel;
using Phantom.Workspaces.Llm.Interfaces.Authorization;

namespace Phantom.Workspaces.Tests.Authorization;

public sealed class HostConfiguredAccessGraphTests
{
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

    [Fact]
    public async Task ConfiguredOAuthRoutesUseOneHostRegistryAndCurrentPresentation()
    {
        await using var host = CreateHost();
        var authentication = new RemoteAuthentication(
            RemoteAuthentication.OAuthScheme,
            Authority: "https://identity.invalid/",
            ClientId: "configured-host-client",
            AuthorizationEndpoint: "https://identity.invalid/authorize",
            TokenEndpoint: "https://identity.invalid/token",
            Scopes: ["tunnel", "profile"],
            AccountEndpoint: "https://identity.invalid/account",
            AccountIdentifierClaim: OAuthAccountIdentifierClaim.Subject,
            Audience: "configured-tunnel-audience");
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
