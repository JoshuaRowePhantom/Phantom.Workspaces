using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.DevTunnels.Contracts;
using Moq;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests.Access;

public sealed class NamedAnonymousDevTunnelConsumerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NamedAnonymous_ActualConsumerRequiresManagementAuthorizationBeforeDiscovery(
        bool persistence,
        bool explicitAnonymousAuthentication)
    {
        var source = Source(explicitAnonymousAuthentication);
        await using var fixture = new Fixture();
        var registry = fixture.Registry;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = fixture.UseConsumerAsync(persistence, source, null, cancellation.Token);
        try
        {
            var row = await PendingAsync(registry, operation);
            Assert.True(row.IsBootstrapBlocking);
            Assert.Contains("Management", row.UseDisplayName, StringComparison.OrdinalIgnoreCase);
            Assert.False(operation.IsCompleted);
            Assert.Empty(fixture.Management.Requests);
            Assert.Empty(fixture.Relay.Requests);
            Assert.Equal(0, fixture.GitHub.Executions);
            await registry.ActAsync(new DenyUserAuthorizationAction
            {
                RequestIdentifier = row.Identifier,
                ExpectedRevision = row.Revision,
            }, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<DevTunnelCredentialAccessException>(() => operation);
        }
        finally
        {
            await cancellation.CancelAsync();
        }
    }

    [Theory]
    [InlineData(CredentialUseKind.Repository, false)]
    [InlineData(CredentialUseKind.Application, false)]
    [InlineData(CredentialUseKind.Repository, true)]
    [InlineData(CredentialUseKind.Application, true)]
    public async Task NamedAnonymous_ActualRootCreatesUnboundNonAnonymousManagementRoute(
        CredentialUseKind useKind,
        bool explicitAnonymousAuthentication)
    {
        await using var host = AccessHostServices.Create(new AccessHostOptions
        {
            PlatformSecretStore = Mock.Of<IPlatformSecretStore>(MockBehavior.Strict),
            Dispatch = action => action(),
            ReleaseMemoryFilePath = Path.Combine(Environment.CurrentDirectory, ".named-anonymous-tests",
                Guid.NewGuid().ToString("N"), "memory.json"),
        });
        var source = Source(explicitAnonymousAuthentication);
        var bootstrap = Assert.IsType<GitHubDevTunnelIdentityRoute>(host.CreateDevTunnelIdentityRoute(source, useKind));
        Assert.Null(bootstrap.Credential.Credential.AccountIdentifier);
        Assert.Equal(GitHubCredentialPurpose.DevTunnelIdentity, bootstrap.Credential.Purpose);
        Assert.Equal(AccessPresentationScope.Bootstrap, bootstrap.Credential.PresentationScope);
        host.EnterRuntime();
        var runtime = Assert.IsType<GitHubDevTunnelIdentityRoute>(host.CreateDevTunnelIdentityRoute(source, useKind));
        Assert.Equal(AccessPresentationScope.Runtime, runtime.Credential.PresentationScope);
        Assert.Equal(bootstrap.Credential.Credential.LogicalIdentifier, runtime.Credential.Credential.LogicalIdentifier);
        Assert.Empty(host.AuthorizationRegistry.GetSnapshot());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task NamedAnonymous_RealBrokerIdentityLeaseDiscoversThenTokenlessRelay(
        bool persistence,
        bool explicitAnonymousAuthentication,
        bool automatic)
    {
        await using var host = CreateHost();
        await using var fixture = new Fixture();
        var source = ProjectSource(explicitAnonymousAuthentication) with
        {
            TunnelName = automatic ? DevTunnelNaming.AutoSelector : "named-anonymous-test",
        };
        fixture.Management.Block = true;
        var route = host.CreateDevTunnelIdentityRoute(
            source, persistence ? CredentialUseKind.Application : CredentialUseKind.Repository);
        Assert.Null(Assert.IsType<GitHubDevTunnelIdentityRoute>(route).Credential.Credential.AccountIdentifier);
        var operation = fixture.UseConsumerAsync(persistence, source, route, TestContext.Current.CancellationToken);
        var management = await PendingAsync(fixture.Registry, operation);
        Assert.True(management.IsBootstrapBlocking);
        Assert.Equal(0, fixture.GitHub.Executions);
        Assert.Empty(fixture.Management.Requests);
        await fixture.AllowAsync(management);
        var provider = await PendingAsync(fixture.Registry, operation);
        Assert.NotEqual(management.Identifier, provider.Identifier);
        Assert.True(provider.IsBootstrapBlocking);
        Assert.Equal(management.UseDisplayName, provider.UseDisplayName);
        Assert.Equal(0, fixture.GitHub.Executions);
        Assert.Empty(fixture.Management.Requests);
        await fixture.AllowAsync(provider);
        await fixture.Management.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Relay.Requests);
        fixture.Scope = AccessPresentationScope.Runtime;
        fixture.Management.Release.TrySetResult();
        var relay = await PendingAsync(fixture.Registry, operation);
        Assert.False(relay.IsBootstrapBlocking);
        Assert.NotEqual(management.UseDisplayName, relay.UseDisplayName);
        Assert.Equal("Anonymous relay access", Assert.Single(relay.SourceOptions).SafeDisplayName);
        Assert.Equal(1, fixture.Management.Disposals);
        Assert.Equal(1, fixture.Management.ContentDisposals);
        await fixture.AllowAsync(relay);
        await operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.GitHub.Executions);
        Assert.Single(fixture.Management.Requests);
        Assert.Equal(2, fixture.Relay.Requests.Count);
        Assert.Equal(1, fixture.Relay.Disposals);
        Assert.All(fixture.Relay.Requests, request => Assert.Equal("https://tsttstst-8080.euw.devtunnels.ms",
            request.RequestUri!.GetLeftPart(UriPartial.Authority)));
        Assert.All(fixture.Management.Requests.Concat(fixture.Relay.Requests), request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-Tunnel-Authorization"));
        });
        Assert.Equal(3, fixture.Registry.GetSnapshot().Count);
        var memory = await fixture.Policy.ListAsync(TestContext.Current.CancellationToken);
        Assert.Single(memory, entry => entry.AccountIdentifier is null);
        Assert.DoesNotContain("management-only-material", JsonSerializer.Serialize(fixture.Registry.GetSnapshot()));
        await fixture.AssertLeasesDisposedAsync();
        fixture.Mint.Verify(session => session.InspectIdentityAsync(It.IsAny<DevTunnelIdentityRoute>(),
            It.IsAny<CancellationToken>()), Times.Never);
        fixture.Mint.Verify(session => session.MintAsync(It.IsAny<DevTunnelConnectCredentialAccessRequest>(),
            It.IsAny<CredentialSourceChoiceDescriptor>(), It.IsAny<ICredentialExecutionContext>(),
            It.IsAny<ProviderAcquisitionKey>(), It.IsAny<ProviderInteractionPermit>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, RemoteAuthentication.GithubScheme)]
    [InlineData(true, RemoteAuthentication.GithubScheme)]
    [InlineData(false, RemoteAuthentication.EntraScheme)]
    [InlineData(true, RemoteAuthentication.EntraScheme)]
    [InlineData(false, RemoteAuthentication.OAuthScheme)]
    [InlineData(true, RemoteAuthentication.OAuthScheme)]
    public async Task NamedAnonymous_ActualConfiguredRootPreservesManagementProvider(
        bool persistence,
        string scheme)
    {
        await using var host = CreateHost();
        await using var fixture = new Fixture();
        var source = ProjectSource(false, Authentication(scheme));
        var kind = persistence ? CredentialUseKind.Application : CredentialUseKind.Repository;
        var configured = host.CreateDevTunnelIdentityRoute(source, kind);
        Assert.NotNull(configured);
        Assert.Equal(scheme, configured.Scheme.ToString(), ignoreCase: true);
        host.EnterRuntime();
        fixture.Scope = host.PresentationScope;
        var identity = EntityRepository.CreateNamedManagementIdentityRequest(
            source, configured, kind, persistence ? "agent-persistence" : "repository", host.GetPresentationScope);
        Assert.Equal(AccessPresentationScope.Runtime, identity.PresentationScope);
        Assert.Null(identity.Credential.AccountIdentifier);
        var relay = EntityRepository.CreateConnectRequest(
            new DevTunnelAccessDescriptor { Access = DevTunnelAccessKind.Anonymous, Name = source.TunnelName },
            source.Authentication, configured, kind, "relay", host.GetPresentationScope);
        Assert.IsType<AnonymousDevTunnelIdentityRoute>(relay.IdentityRoute);
        Assert.NotEqual(identity.Use.Fingerprint, relay.Use.Fingerprint);
        Assert.Equal(identity.Use.Lineage, relay.Use.Lineage);
        var operation = fixture.UseConsumerAsync(persistence, source, configured, TestContext.Current.CancellationToken);
        await fixture.AllowAsync(await PendingAsync(fixture.Registry, operation));
        await fixture.AllowAsync(await PendingAsync(fixture.Registry, operation));
        await fixture.AllowAsync(await PendingAsync(fixture.Registry, operation));
        await operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(scheme switch
        {
            RemoteAuthentication.GithubScheme => "github",
            RemoteAuthentication.EntraScheme => "aad",
            _ => "Bearer",
        }, fixture.Management.IdentityScheme);
        Assert.All(fixture.Registry.GetSnapshot(), row => Assert.False(row.IsBootstrapBlocking));
        Assert.Equal(1, fixture.Management.Disposals);
        Assert.Equal(1, fixture.Relay.Disposals);
        await fixture.AssertLeasesDisposedAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NamedAnonymous_ProductionGitHubExecutorVerifiesUnboundAccountBeforeManagement(
        bool persistence,
        bool explicitAnonymousAuthentication)
    {
        await using var host = CreateHost();
        await using var fixture = new Fixture(productionGitHub: true);
        var source = ProjectSource(explicitAnonymousAuthentication);
        var route = host.CreateDevTunnelIdentityRoute(
            source, persistence ? CredentialUseKind.Application : CredentialUseKind.Repository);
        Assert.Null(Assert.IsType<GitHubDevTunnelIdentityRoute>(route).Credential.Credential.AccountIdentifier);
        var operation = fixture.UseConsumerAsync(persistence, source, route, TestContext.Current.CancellationToken);
        await fixture.AllowAsync(await PendingAsync(fixture.Registry, operation));
        var child = await PendingAsync(fixture.Registry, operation);
        Assert.Equal(0, fixture.GitHubSource.Acquisitions);
        Assert.Empty(fixture.Management.Requests);
        await fixture.AllowAsync(child);
        var relay = await PendingAsync(fixture.Registry, operation);
        Assert.Equal(1, fixture.GitHubSource.Acquisitions);
        Assert.All(fixture.GitHubSource.Material!, character => Assert.Equal('\0', character));
        var memory = await fixture.Policy.ListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, memory.Count);
        Assert.All(memory, entry => Assert.Equal(fixture.GitHubSource.Account, entry.AccountIdentifier));
        Assert.Equal(1, fixture.Management.Disposals);
        Assert.Equal(1, fixture.Management.ContentDisposals);
        await fixture.AllowAsync(relay);
        await operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.GitHubSource.Acquisitions);
        Assert.Equal(2, fixture.Relay.Requests.Count);
        Assert.Equal(1, fixture.Relay.Disposals);
        Assert.Single(await fixture.ProductionGitHub!.ListAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NamedAnonymous_ActualRootBrokerOwnsConfiguredManagementAuthorization(
        bool persistence,
        bool runtime)
    {
        await using var host = CreateHost();
        var source = ProjectSource(false, Authentication(RemoteAuthentication.OAuthScheme));
        var route = Assert.IsType<OAuthDevTunnelIdentityRoute>(host.CreateDevTunnelIdentityRoute(
            source, persistence ? CredentialUseKind.Application : CredentialUseKind.Repository));
        Assert.NotNull(host.OAuthProviders.FindConfiguration(route.Credential.Credential.LogicalIdentifier));
        var management = new ManagementTransport();
        if (runtime) { host.EnterRuntime(); }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task operation = persistence
            ? new AgentPersistenceStoreSourceFactory(
                source, host.Broker, route, host.GetPresentationScope,
                Mock.Of<IAuthenticatedHttpClientFactory>(MockBehavior.Strict), () => management).CreateAsync(cancellation.Token)
            : CreateRepositoryAsync();
        try
        {
            var ready = new TaskCompletionSource<UserAuthorizationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Find()
            {
                var pending = host.AuthorizationRegistry.GetSnapshot().LastOrDefault(row => row.State == UserAuthorizationState.Pending);
                if (pending is not null) { ready.TrySetResult(pending); }
            }
            EventHandler<UserAuthorizationChangedEventArgs> changed = (_, _) => Find();
            host.AuthorizationRegistry.Changed += changed;
            UserAuthorizationSnapshot row;
            try
            {
                Find();
                row = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            finally
            {
                host.AuthorizationRegistry.Changed -= changed;
            }
            Assert.Equal(!runtime, row.IsBootstrapBlocking);
            Assert.Equal(row.Identifier, Assert.Single(host.AuthorizationRows.Rows).Identifier);
            Assert.Contains("Management", row.UseDisplayName, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(management.Requests);
            await host.AuthorizationRegistry.ActAsync(new DenyUserAuthorizationAction
            {
                RequestIdentifier = row.Identifier,
                ExpectedRevision = row.Revision,
            }, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<DevTunnelCredentialAccessException>(() => operation);
            Assert.Empty(management.Requests);
            Assert.Equal(1, management.Disposals);
        }
        finally
        {
            await cancellation.CancelAsync();
        }

        async Task<object> CreateRepositoryAsync() =>
            await EntityRepository.CreateDevTunnelNameDataAccessLayerAsync(
                source, host.Broker, route, host.GetPresentationScope, cancellation.Token,
                Mock.Of<IAuthenticatedHttpClientFactory>(MockBehavior.Strict), () => management);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedAnonymous_DeniedRelayCannotFallBackToIdentity(bool persistence)
    {
        await using var fixture = new Fixture();
        var operation = fixture.UseConsumerAsync(persistence, Source(false), null, TestContext.Current.CancellationToken);
        await fixture.AllowAsync(await PendingAsync(fixture.Registry, operation));
        await fixture.AllowAsync(await PendingAsync(fixture.Registry, operation));
        var relay = await PendingAsync(fixture.Registry, operation);
        await fixture.Registry.ActAsync(new DenyUserAuthorizationAction
        {
            RequestIdentifier = relay.Identifier,
            ExpectedRevision = relay.Revision,
        }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DevTunnelCredentialAccessException>(() => operation);
        Assert.Single(fixture.Management.Requests);
        Assert.Empty(fixture.Relay.Requests);
        Assert.Equal(1, fixture.Relay.Disposals);
        await fixture.AssertLeasesDisposedAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NamedAnonymous_DeniedProviderOrPendingShutdownNeverSends(
        bool persistence,
        bool shutdown)
    {
        await using var fixture = new Fixture();
        var operation = fixture.UseConsumerAsync(persistence, Source(false), null, TestContext.Current.CancellationToken);
        await fixture.AllowAsync(await PendingAsync(fixture.Registry, operation));
        var provider = await PendingAsync(fixture.Registry, operation);
        if (shutdown)
        {
            await fixture.Broker.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        else
        {
            await fixture.Registry.ActAsync(new DenyUserAuthorizationAction
            {
                RequestIdentifier = provider.Identifier,
                ExpectedRevision = provider.Revision,
            }, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<DevTunnelCredentialAccessException>(() => operation);
        }
        Assert.Empty(fixture.Management.Requests);
        Assert.Empty(fixture.Relay.Requests);
        Assert.Equal(0, fixture.GitHub.Executions);
        Assert.Equal(1, fixture.Management.Disposals);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NamedAnonymous_CancellationOrShutdownDiscardsLateManagementResponse(
        bool persistence,
        bool shutdown)
    {
        await using var fixture = new Fixture();
        fixture.Management.Block = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = fixture.UseConsumerAsync(persistence, Source(false), null, cancellation.Token);
        await fixture.AllowAsync(await PendingAsync(fixture.Registry, operation));
        await fixture.AllowAsync(await PendingAsync(fixture.Registry, operation));
        await fixture.Management.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var stopped = shutdown ? fixture.Broker.DisposeAsync().AsTask() : cancellation.CancelAsync();
        fixture.Management.Release.TrySetResult();
        await stopped;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Relay.Requests);
        Assert.Equal(1, fixture.Management.Disposals);
        Assert.All(fixture.Management.Requests, request => Assert.Null(request.Headers.Authorization));
        Assert.Equal(1, fixture.Management.ContentDisposals);
        await fixture.AssertLeasesDisposedAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NamedAnonymous_ManagementFailuresRemainTypedAndDoNotReachRelay(
        bool persistence,
        bool malformed)
    {
        await using var fixture = new Fixture();
        fixture.Management.Malformed = malformed;
        fixture.Management.Fail = !malformed;
        var operation = fixture.UseConsumerAsync(persistence, Source(false), null, TestContext.Current.CancellationToken);
        await fixture.AllowAsync(await PendingAsync(fixture.Registry, operation));
        await fixture.AllowAsync(await PendingAsync(fixture.Registry, operation));
        var failure = await Assert.ThrowsAsync<DevTunnelCredentialAccessException>(() => operation);
        Assert.Equal(AccessErrorCode.ProviderFailure, failure.ErrorCode);
        Assert.DoesNotContain("sensitive", failure.ToString());
        Assert.Empty(fixture.Relay.Requests);
        Assert.Equal(1, fixture.Management.Disposals);
        await fixture.AssertLeasesDisposedAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedAnonymous_ManagementSdkRedirectNeverExportsIdentityToRelay(bool persistence)
    {
        await using var fixture = new Fixture();
        fixture.Management.Redirect = true;
        var operation = fixture.UseConsumerAsync(persistence, Source(false), null, TestContext.Current.CancellationToken);
        await fixture.AllowAsync(await PendingAsync(fixture.Registry, operation));
        await fixture.AllowAsync(await PendingAsync(fixture.Registry, operation));
        var failure = await Assert.ThrowsAsync<DevTunnelCredentialAccessException>(() => operation);
        Assert.Equal(AccessErrorCode.IdentityMismatch, failure.ErrorCode);
        Assert.Single(fixture.Management.Requests);
        Assert.Empty(fixture.Relay.Requests);
        Assert.Null(fixture.Management.Requests[0].Headers.Authorization);
        Assert.Equal(1, fixture.Management.Disposals);
        await fixture.AssertLeasesDisposedAsync();
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "ambiguous")]
    [InlineData(true, "ambiguous")]
    [InlineData(false, "ports")]
    [InlineData(true, "ports")]
    [InlineData(false, "unsafe")]
    [InlineData(true, "unsafe")]
    [InlineData(false, "port-zero")]
    [InlineData(true, "port-zero")]
    public async Task NamedAnonymous_InvalidDiscoveryNeverBuildsRelay(bool persistence, string invalid)
    {
        await using var fixture = new Fixture();
        var tunnel = fixture.Management.Tunnels[0];
        fixture.Management.Tunnels = invalid switch
        {
            "missing" => [new Tunnel { TunnelId = tunnel.TunnelId, ClusterId = tunnel.ClusterId, Ports = tunnel.Ports,
                Labels = ["named-anonymous-test"] }],
            "ambiguous" => [tunnel, tunnel],
            "ports" => [new Tunnel { TunnelId = tunnel.TunnelId, ClusterId = tunnel.ClusterId, Labels = tunnel.Labels,
                Ports = [new TunnelPort { PortNumber = 8080 }, new TunnelPort { PortNumber = 8081 }] }],
            "port-zero" => [new Tunnel { TunnelId = tunnel.TunnelId, ClusterId = tunnel.ClusterId, Labels = tunnel.Labels,
                Ports = [new TunnelPort { PortNumber = 0 }] }],
            _ => [new Tunnel { TunnelId = "unsafe/path", ClusterId = tunnel.ClusterId, Labels = tunnel.Labels, Ports = tunnel.Ports }],
        };
        var operation = fixture.UseConsumerAsync(persistence, Source(false), null, TestContext.Current.CancellationToken);
        await fixture.AllowAsync(await PendingAsync(fixture.Registry, operation));
        await fixture.AllowAsync(await PendingAsync(fixture.Registry, operation));
        var failure = await Assert.ThrowsAsync<DevTunnelCredentialAccessException>(() => operation);
        Assert.Equal(AccessErrorCode.IdentityMismatch, failure.ErrorCode);
        Assert.Empty(fixture.Relay.Requests);
        Assert.Equal(0, fixture.Relay.Disposals);
        Assert.Equal(1, fixture.Management.Disposals);
        await fixture.AssertLeasesDisposedAsync();
    }

    private static AccessHostServices CreateHost() => AccessHostServices.Create(new AccessHostOptions
    {
        PlatformSecretStore = Mock.Of<IPlatformSecretStore>(MockBehavior.Strict),
        Dispatch = action => action(),
        ReleaseMemoryFilePath = Path.Combine(Environment.CurrentDirectory, ".named-anonymous-tests",
            Guid.NewGuid().ToString("N"), "root-memory.json"),
    });

    private static RemoteAuthentication Authentication(string scheme) => new(
        scheme,
        Authority: "https://identity.invalid/",
        ClientId: "test-management-client",
        AuthorizationEndpoint: "https://identity.invalid/authorize",
        TokenEndpoint: "https://identity.invalid/token",
        AccountEndpoint: "https://identity.invalid/account",
        AccountIdentifierClaim: OAuthAccountIdentifierClaim.Subject,
        Audience: "https://tunnels.api.visualstudio.com/",
        Scopes: ["tunnel"]);

    private static DevTunnelNameRepositorySource ProjectSource(
        bool explicitAnonymousAuthentication,
        RemoteAuthentication? managementAuthentication = null) =>
        Assert.IsType<DevTunnelNameRepositorySource>(new WorkspacesConfiguration
        {
            DataAccess = new DataAccessConnectionProfile
            {
                Mode = DataAccessMode.DevTunnelWeb,
                Authentication = explicitAnonymousAuthentication
                    ? new RemoteAuthentication(RemoteAuthentication.AnonymousScheme) : managementAuthentication,
            },
            DevTunnel = new DevTunnelConfiguration
            {
                TunnelName = "named-anonymous-test",
                AccessMode = explicitAnonymousAuthentication ? DevTunnelAccessMode.Private : DevTunnelAccessMode.Anonymous,
            },
        }.ToRepositorySource());

    private static DevTunnelNameRepositorySource Source(bool explicitAnonymousAuthentication) => new(
        "named-anonymous-test",
        explicitAnonymousAuthentication ? DevTunnelAccessMode.Private : DevTunnelAccessMode.Anonymous,
        explicitAnonymousAuthentication ? new RemoteAuthentication(RemoteAuthentication.AnonymousScheme) : null);

    private static async Task<UserAuthorizationSnapshot> PendingAsync(
        UserAuthorizationRegistry registry,
        Task operation)
    {
        var ready = new TaskCompletionSource<UserAuthorizationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Find()
        {
            var row = registry.GetSnapshot().LastOrDefault(candidate => candidate.State == UserAuthorizationState.Pending);
            if (row is not null)
            {
                ready.TrySetResult(row);
            }
        }
        EventHandler<UserAuthorizationChangedEventArgs> changed = (_, _) => Find();
        registry.Changed += changed;
        try
        {
            Find();
            if (await Task.WhenAny(ready.Task, operation).WaitAsync(TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken) == operation)
            {
                await operation;
                Assert.Fail("Consumer completed without the required authorization.");
            }
            return await ready.Task;
        }
        finally
        {
            registry.Changed -= changed;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Environment.CurrentDirectory,
            ".named-anonymous-tests", Guid.NewGuid().ToString("N"));

        public Fixture(bool productionGitHub = false)
        {
            this.Policy = new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, "memory.json")));
            var executors = new CredentialAccessExecutorRegistry();
            this.Mint.Setup(session => session.DisposeAsync()).Returns(ValueTask.CompletedTask);
            if (productionGitHub)
            {
                this.ProductionGitHub = new GitHubCredentialAccessExecutor(this.GitHubSource);
            }
            ICredentialAccessExecutor<GitHubCredentialAccessRequest, GitHubCredentialAccessResult> gitHub =
                this.ProductionGitHub is { } actual ? actual : this.GitHub;
            executors.Add<GitHubCredentialAccessRequest, GitHubCredentialAccessResult>(gitHub);
            executors.Add<EntraCredentialAccessRequest, EntraCredentialAccessResult>(this.Entra);
            executors.Add<OAuthCredentialAccessRequest, OAuthCredentialAccessResult>(this.OAuth);
            executors.Add<DevTunnelIdentityCredentialAccessRequest, DevTunnelIdentityCredentialAccessResult>(
                new DevTunnelIdentityCredentialAccessExecutor(gitHub, this.Entra, this.OAuth));
            executors.Add<DevTunnelConnectCredentialAccessRequest, DevTunnelConnectCredentialAccessResult>(
                new DevTunnelConnectCredentialAccessExecutor(this.Mint.Object, this.Registry));
            this.Broker = new AccessBroker(this.Policy, executors, new ProviderAcquisitionCoordinator(), this.Registry);
        }

        public ReleaseAuthorizationPolicy Policy { get; }
        public UserAuthorizationRegistry Registry { get; } = new();
        public AccessBroker Broker { get; }
        public GitHubCredentialAccessExecutor? ProductionGitHub { get; }
        public TestGitHubSource GitHubSource { get; } = new();
        public Mock<IDevTunnelConnectMintSession> Mint { get; } = new(MockBehavior.Strict);
        public DependencyExecutor<GitHubCredentialAccessRequest, GitHubCredentialAccessResult> GitHub { get; } =
            new(CredentialExecutorKind.GitHub, lease => new GitHubCredentialAccessResult { Lease = lease });
        public DependencyExecutor<EntraCredentialAccessRequest, EntraCredentialAccessResult> Entra { get; } =
            new(CredentialExecutorKind.Entra, lease => new EntraCredentialAccessResult { AccessToken = lease, ExpiresOn = DateTimeOffset.MaxValue });
        public DependencyExecutor<OAuthCredentialAccessRequest, OAuthCredentialAccessResult> OAuth { get; } =
            new(CredentialExecutorKind.OAuth, lease => new OAuthCredentialAccessResult { AccessToken = lease });
        public ManagementTransport Management { get; } = new();
        public RelayTransport Relay { get; } = new();
        public AccessPresentationScope Scope { get; set; } = AccessPresentationScope.Bootstrap;

        public async Task UseConsumerAsync(
            bool persistence,
            DevTunnelNameRepositorySource source,
            DevTunnelIdentityRoute? route,
            CancellationToken cancellationToken)
        {
            var clients = new AuthenticatedHttpClientFactory(() => this.Relay);
            if (persistence)
            {
                var store = await new AgentPersistenceStoreSourceFactory(source, this.Broker, route,
                    () => this.Scope, clients, () => this.Management).CreateAsync(cancellationToken);
                await using var owned = (IAsyncDisposable)store;
                Assert.Empty(await store.ReadSubAgentChildIdsAsync("test-session", cancellationToken));
            }
            else
            {
                var layer = await EntityRepository.CreateDevTunnelNameDataAccessLayerAsync(
                    source, this.Broker, route, () => this.Scope, cancellationToken, clients, () => this.Management);
                await using var owned = (IAsyncDisposable)layer;
                Assert.Empty((await layer.GetAsync(new GetRequest { Entities = [] }, cancellationToken)).Batches);
            }
        }

        public Task AllowAsync(UserAuthorizationSnapshot row) => this.Registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Memory = row.MemoryOptions.Single(option => option.Scope == ReleaseMemoryScopeKind.AllUses).Identifier,
            Source = (row.SourceOptions.SingleOrDefault(source => source.Kind == CredentialSourceKind.GitHubCli)
                ?? row.SourceOptions.Single()).Identifier,
        }, TestContext.Current.CancellationToken);

        public async Task AssertLeasesDisposedAsync()
        {
            foreach (var lease in this.GitHub.Leases.Concat(this.Entra.Leases).Concat(this.OAuth.Leases))
            {
                Assert.IsType<SensitiveValueUseFailed<int>>(await lease.UseAsync<int>(
                    (_, _) => throw new InvalidOperationException("Disposed identity must never expose material."),
                    TestContext.Current.CancellationToken));
            }
        }

        public sealed class TestGitHubSource : IGitHubCredentialAccessSource
        {
            public int Acquisitions { get; private set; }
            public char[]? Material { get; private set; }
            public CredentialAccountIdentifier Account { get; } = CredentialIdentityFactory.CreateAccountIdentifier(
                CredentialProviderKind.GitHub, "verified-test-management-account");

            public bool EnvironmentExists() => false;

            public ValueTask<GitHubCredentialMaterial?> AcquireAsync(
                CredentialSourceKind source,
                ProviderInteractionPermit permit,
                ProviderAcquisitionKey key,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Assert.Equal(CredentialSourceKind.GitHubCli, source);
                Assert.True(permit.AuthorizesProvider(CredentialExecutorKind.GitHub, key));
                Assert.Null(key.Account);
                this.Acquisitions++;
                this.Material = "management-only-material".ToCharArray();
                return ValueTask.FromResult<GitHubCredentialMaterial?>(new GitHubCredentialMaterial
                {
                    Material = this.Material,
                    Account = this.Account,
                    ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            this.Management.Release.TrySetResult();
            await this.Broker.DisposeAsync();
            if (Directory.Exists(this.directory))
            {
                Directory.Delete(this.directory, recursive: true);
            }
        }
    }

    private sealed class DependencyExecutor<TRequest, TResult>(
        CredentialExecutorKind kind,
        Func<SensitiveValueLease, TResult> createResult) :
        ICredentialAccessExecutor<TRequest, TResult>, ICredentialGenerationValidator
        where TRequest : CredentialAccessRequest<TResult>
        where TResult : ICredentialAccessResult
    {
        private readonly CredentialMaterialGenerationIdentifier generation = new() { Value = Guid.NewGuid() };
        public CredentialExecutorKind Kind => kind;
        public int Executions { get; private set; }
        public List<SensitiveValueLease> Leases { get; } = [];

        public ValueTask<CredentialInspection> InspectAsync(TRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CredentialInspection
            {
                InspectionIdentifier = Guid.NewGuid(),
                Credential = request.Credential with
                {
                    AccountIdentifier = CredentialIdentityFactory.CreateAccountIdentifier(request.Credential.Provider, "verified-test-management-account"),
                },
                Sources = request.Sources.Where(source => kind != CredentialExecutorKind.GitHub ||
                    source.Kind == CredentialSourceKind.GitHubCli).Select(source => source with
                    { Usability = CredentialSourceUsability.Usable }).ToArray(),
            });

        public ValueTask<CredentialExecutionOutcome<TResult>> ExecuteAsync(
            TRequest request,
            CredentialInspection inspection,
            ReleaseAuthorization authorization,
            ProviderInteractionPermit? interactionPermit,
            ICredentialExecutionContext context,
            CancellationToken cancellationToken)
        {
            this.Executions++;
            return ValueTask.FromResult<CredentialExecutionOutcome<TResult>>(new CredentialExecutionReady<TResult>
            {
                Materializer = new Materializer(() =>
                {
                    var lease = new SensitiveValueLease("management-only-material".AsMemory(), new CredentialLeaseBinding
                    {
                        AuthorizationIdentifier = authorization.Identifier,
                        Request = context.PolicyRequest!,
                        MaterialGeneration = this.generation,
                    }, context.CreateLeaseContext(this));
                    this.Leases.Add(lease);
                    return createResult(lease);
                }),
            });
        }

        public ValueTask<bool> IsCurrentGenerationAsync(
            CredentialMaterialGenerationIdentifier candidate,
            CancellationToken cancellationToken) => ValueTask.FromResult(candidate == this.generation);

        private sealed class Materializer(Func<TResult> create) : IAccessResultMaterializer<TResult>
        {
            public ValueTask<TResult> MaterializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(create());
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ManagementTransport : DelegatingHandler
    {
        public ManagementTransport() : base(new HttpClientHandler { AllowAutoRedirect = false }) { }

        public List<HttpRequestMessage> Requests { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block { get; set; }
        public bool Redirect { get; set; }
        public bool Malformed { get; set; }
        public bool Fail { get; set; }
        public string? IdentityScheme { get; private set; }
        public int Disposals { get; private set; }
        public int ContentDisposals { get; private set; }
        public Tunnel[] Tunnels { get; set; } =
        [
            new Tunnel
            {
                TunnelId = "tsttstst",
                ClusterId = "euw",
                Labels = [DevTunnelNaming.WorkspacesMarkerLabel, "named-anonymous-test"],
                Ports = [new TunnelPort { PortNumber = 8080 }],
            },
        ];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            Assert.EndsWith(".rel.tunnels.api.visualstudio.com", request.RequestUri!.Host);
            Assert.Equal("management-only-material", request.Headers.Authorization?.Parameter);
            this.IdentityScheme = request.Headers.Authorization!.Scheme;
            Assert.False(request.Headers.Contains("X-Tunnel-Authorization"));
            Assert.DoesNotContain("tokenScopes", request.RequestUri.Query);
            this.Entered.TrySetResult();
            if (this.Block)
            {
                await this.Release.Task;
            }
            if (this.Fail) { throw new HttpRequestException("sensitive test transport failure"); }
            if (this.Redirect)
            {
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    RequestMessage = request,
                    Headers = { Location = new Uri("https://tsttstst-8080.euw.devtunnels.ms/") },
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new TrackedContent(this.Malformed ? "sensitive invalid json" : JsonSerializer.Serialize(
                    new { value = new[] { new { value = this.Tunnels } } },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)), () => this.ContentDisposals++),
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { this.Disposals++; }
            base.Dispose(disposing);
        }
    }

    private sealed class TrackedContent(string content, Action disposed) : StringContent(content, Encoding.UTF8, "application/json")
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing) { disposed(); }
            base.Dispose(disposing);
        }
    }

    private sealed class RelayTransport : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public int Disposals { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-Tunnel-Authorization"));
            var restore = request.RequestUri!.AbsolutePath == "/agent/persistence/restore";
            return Task.FromResult(new HttpResponseMessage(restore ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(request.RequestUri.AbsolutePath == "/data/get"
                    ? """{"batches":[]}""" : """{"childSessionIds":[]}"""),
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { this.Disposals++; }
            base.Dispose(disposing);
        }
    }
}
