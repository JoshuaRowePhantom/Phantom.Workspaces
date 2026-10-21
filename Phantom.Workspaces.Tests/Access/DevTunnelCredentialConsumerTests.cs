using System.IO;
using System.Net;
using System.Net.Http;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Web.Client;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Services.Authorization;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests.Access;

public sealed class DevTunnelCredentialConsumerTests : IDisposable
{
    private static readonly Uri Relay = new("https://consumer-test.euw.devtunnels.ms/");
    private readonly string directory = Path.Combine(Environment.CurrentDirectory, ".access-relay-consumer-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EntityRepository_PrivateEndpoint_UsesOnlyConnectLeaseAroundActualSend()
    {
        var session = new MintSession();
        var (broker, registry) = this.Create(session);
        await using var owned = broker;
        var transport = new Transport();
        using var layer = (WebClientDataAccessLayer)EntityRepository.CreateWebDataAccessLayer(
            new WebRepositorySource(Relay.AbsoluteUri, UseGitHubAuthToken: true),
            broker, null, () => AccessPresentationScope.Bootstrap, new AuthenticatedHttpClientFactory(() => transport));
        var task = layer.GetAsync(new GetRequest { Entities = [] }, TestContext.Current.CancellationToken);
        Assert.Equal(0, transport.Sends);
        Assert.Equal(0, session.Mints);
        Assert.True(Assert.Single(registry.GetSnapshot()).IsBootstrapBlocking);
        await AllowAsync(registry);
        Assert.Empty((await task).Batches);
        Assert.Equal("tunnel connect-consumer-material", transport.LastTunnelAuthorization);
        Assert.Null(transport.LastAuthorization);
        Assert.Null(transport.LastRequest!.Headers.Authorization);
        Assert.False(transport.LastRequest.Headers.Contains("X-Tunnel-Authorization"));
        Assert.Equal(1, session.Mints);
        Assert.Single(registry.GetSnapshot());
        layer.Dispose();
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task Persistence_PrivateEndpoint_UsesConnectAndReadsCurrentPresentationScope()
    {
        var session = new MintSession();
        var (broker, registry) = this.Create(session);
        await using var owned = broker;
        var transport = new Transport();
        var scope = AccessPresentationScope.Bootstrap;
        var store = await new AgentPersistenceStoreSourceFactory(
            new WebRepositorySource(Relay.AbsoluteUri, UseGitHubAuthToken: true),
            broker, null, () => scope, new AuthenticatedHttpClientFactory(() => transport)).CreateAsync(TestContext.Current.CancellationToken);
        using var storeDisposal = (IDisposable)store;
        var first = store.ReadSubAgentChildIdsAsync("first", TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(registry.GetSnapshot()).IsBootstrapBlocking);
        Assert.Equal(0, transport.Sends);
        await AllowAsync(registry);
        Assert.Empty(await first);
        scope = AccessPresentationScope.Runtime;
        var second = store.ReadSubAgentChildIdsAsync("second", TestContext.Current.CancellationToken);
        Assert.False(registry.GetSnapshot().Last().IsBootstrapBlocking);
        await AllowAsync(registry);
        Assert.Empty(await second);
        Assert.Equal(2, transport.Sends);
        Assert.Equal(1, session.Mints);
        Assert.Equal("tunnel connect-consumer-material", transport.LastTunnelAuthorization);
        Assert.Null(transport.LastAuthorization);
        storeDisposal.Dispose();
        Assert.True(transport.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedConsumer_ResolvesThroughConnectThenUsesOnlySafeEndpointMetadata(bool persistence)
    {
        var session = new MintSession();
        var (broker, registry) = this.Create(session);
        await using var owned = broker;
        var transport = new Transport();
        var factory = new AuthenticatedHttpClientFactory(() => transport);
        var source = new DevTunnelNameRepositorySource("consumer-test", Configuration.DevTunnelAccessMode.Private);
        Task operation;
        if (persistence)
        {
            operation = this.UseNamedPersistenceAsync(source, broker, factory);
        }
        else
        {
            operation = this.UseNamedRepositoryAsync(source, broker, factory);
        }
        Assert.Equal(0, transport.Sends);
        Assert.Equal(0, session.Mints);
        await AllowNextAsync(registry, operation);
        await AllowNextAsync(registry, operation);
        await AllowNextAsync(registry, operation);
        await operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, session.Mints);
        Assert.Equal(2, transport.Sends);
        Assert.Equal("tunnel connect-consumer-material", transport.LastTunnelAuthorization);
        Assert.Null(transport.LastAuthorization);
        Assert.Equal(3, registry.GetSnapshot().Count);
        Assert.True(transport.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingPrivateCallerCancellationOrShutdownNeverSends(bool shutdown)
    {
        var session = new MintSession();
        var (broker, registry) = this.Create(session);
        await using var owned = broker;
        var transport = new Transport();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var layer = (WebClientDataAccessLayer)EntityRepository.CreateWebDataAccessLayer(
            new WebRepositorySource(Relay.AbsoluteUri, UseGitHubAuthToken: true),
            broker, null, () => AccessPresentationScope.Runtime, new AuthenticatedHttpClientFactory(() => transport));
        var pending = layer.GetAsync(new GetRequest { Entities = [] }, cancellation.Token);
        await WaitForPendingAsync(registry, pending);

        if (shutdown)
        {
            await broker.DisposeAsync();
        }
        else
        {
            await cancellation.CancelAsync();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.Sends);
        Assert.Equal(0, session.Mints);
        Assert.DoesNotContain(registry.GetSnapshot(), row => row.State == UserAuthorizationState.Pending);
    }

    [Fact]
    public async Task DeniedConnect_NeverSendsIdentityOrUnauthenticatedPrivateRequest()
    {
        var session = new MintSession();
        var (broker, registry) = this.Create(session);
        await using var owned = broker;
        var transport = new Transport();
        using var layer = (WebClientDataAccessLayer)EntityRepository.CreateWebDataAccessLayer(
            new WebRepositorySource(Relay.AbsoluteUri, UseGitHubAuthToken: true),
            broker, null, () => AccessPresentationScope.Runtime, new AuthenticatedHttpClientFactory(() => transport));
        var task = layer.GetAsync(new GetRequest { Entities = [] }, TestContext.Current.CancellationToken);
        var row = Assert.Single(registry.GetSnapshot());
        await registry.ActAsync(new DenyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
        }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DevTunnelCredentialAccessException>(() => task);
        Assert.Equal(0, transport.Sends);
        Assert.Equal(0, session.Mints);
    }

    [Fact]
    public async Task PlainAndExplicitAnonymousWeb_DoNotNeedBrokerOrResolveIdentity()
    {
        using var plain = Assert.IsType<WebClientDataAccessLayer>(EntityRepository.CreateWebDataAccessLayer(
            new WebRepositorySource("https://web.example/"), null, null, null));
        var source = new WebRepositorySource("https://web.example/", UseGitHubAuthToken: true,
            Authentication: new RemoteAuthentication(RemoteAuthentication.AnonymousScheme));
        using var anonymous = Assert.IsType<WebClientDataAccessLayer>(EntityRepository.CreateWebDataAccessLayer(source, null, null, null));
        using var store = Assert.IsType<WebClientAgentPersistenceStore>(await new AgentPersistenceStoreSourceFactory(source).CreateAsync(TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    private async Task UseNamedRepositoryAsync(DevTunnelNameRepositorySource source, IAccessBroker broker, IAuthenticatedHttpClientFactory factory)
    {
        var layer = await EntityRepository.CreateDevTunnelNameDataAccessLayerAsync(
            source, broker, null, () => AccessPresentationScope.Runtime, TestContext.Current.CancellationToken, factory);
        await using var layerDisposal = (IAsyncDisposable)layer;
        await layer.GetAsync(new GetRequest { Entities = [] }, TestContext.Current.CancellationToken);
    }

    private async Task UseNamedPersistenceAsync(DevTunnelNameRepositorySource source, IAccessBroker broker, IAuthenticatedHttpClientFactory factory)
    {
        var store = await new AgentPersistenceStoreSourceFactory(source, broker, null, () => AccessPresentationScope.Runtime, factory).CreateAsync(TestContext.Current.CancellationToken);
        await using var storeDisposal = (IAsyncDisposable)store;
        await store.ReadSubAgentChildIdsAsync("session", TestContext.Current.CancellationToken);
    }

    private (AccessBroker Broker, UserAuthorizationRegistry Registry) Create(MintSession session)
    {
        var registry = new UserAuthorizationRegistry();
        var executors = new CredentialAccessExecutorRegistry();
        executors.Add<DevTunnelConnectCredentialAccessRequest, DevTunnelConnectCredentialAccessResult>(
            new DevTunnelConnectCredentialAccessExecutor(session, registry));
        return (new AccessBroker(new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(this.directory, "memory.json"))),
            executors, new ProviderAcquisitionCoordinator(), registry), registry);
    }

    private static async Task AllowAsync(UserAuthorizationRegistry registry)
    {
        var row = registry.GetSnapshot().Last(snapshot => snapshot.State == UserAuthorizationState.Pending);
        await registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Memory = row.MemoryOptions.Single(option => option.Scope == ReleaseMemoryScopeKind.AllowOnce).Identifier,
            Source = row.SourceOptions.Single(source => source.Kind == CredentialSourceKind.GitHubCli).Identifier,
        }, TestContext.Current.CancellationToken);
    }

    private static async Task AllowNextAsync(UserAuthorizationRegistry registry, Task operation)
    {
        await WaitForPendingAsync(registry, operation);
        await AllowAsync(registry);
    }

    private static async Task<UserAuthorizationSnapshot> WaitForPendingAsync(
        UserAuthorizationRegistry registry,
        Task operation)
    {
        var ready = new TaskCompletionSource<UserAuthorizationSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Find()
        {
            var row = registry.GetSnapshot().LastOrDefault(snapshot => snapshot.State == UserAuthorizationState.Pending);
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
            var completed = await Task.WhenAny(ready.Task, operation)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (completed == operation)
            {
                await operation;
                Assert.Fail("The consumer completed before the expected authorization request.");
            }
            return await ready.Task;
        }
        finally
        {
            registry.Changed -= changed;
        }
    }

    private sealed class MintSession : IDevTunnelConnectMintSession
    {
        private CredentialAccountIdentifier? account;
        public int Mints { get; private set; }

        public ValueTask<CredentialInspection> InspectIdentityAsync(DevTunnelIdentityRoute route, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new CredentialInspection
            {
                InspectionIdentifier = Guid.NewGuid(),
                Credential = route.Reference! with { AccountIdentifier = this.account },
                Sources = route.Sources,
            });

        public ValueTask<DevTunnelConnectMintOutcome> MintAsync(
            DevTunnelConnectCredentialAccessRequest request,
            CredentialSourceChoiceDescriptor identitySource,
            ICredentialExecutionContext context,
            ProviderAcquisitionKey parentAcquisitionKey,
            ProviderInteractionPermit parentGenerationPermit,
            CancellationToken cancellationToken)
        {
            Assert.Equal(CredentialSourceKind.GitHubCli, identitySource.Kind);
            Assert.Equal(request.Credential.LogicalIdentifier, parentAcquisitionKey.Configuration);
            Assert.Equal(context.PolicyRequest!.SourceIdentifier, parentAcquisitionKey.Source);
            Assert.Equal(context.PolicyRequest.Credential.AccountIdentifier, parentAcquisitionKey.Account);
            Assert.Equal(ProviderProtocolKind.TunnelConnect, parentAcquisitionKey.Protocol);
            Assert.True(parentGenerationPermit.AuthorizesProvider(CredentialExecutorKind.DevTunnelConnect, parentAcquisitionKey));
            this.Mints++;
            this.account = CredentialIdentityFactory.CreateAccountIdentifier(CredentialProviderKind.GitHub, "verified-consumer-principal");
            return ValueTask.FromResult<DevTunnelConnectMintOutcome>(new DevTunnelConnectMintSucceeded
            {
                Material = "connect-consumer-material".ToCharArray(),
                Account = this.account,
                ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(5),
                Endpoint = request.Tunnel.Endpoint ?? Relay,
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Transport : HttpMessageHandler
    {
        public int Sends { get; private set; }
        public string? LastTunnelAuthorization { get; private set; }
        public string? LastAuthorization { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Sends++;
            this.LastRequest = request;
            this.LastAuthorization = request.Headers.Authorization?.ToString();
            this.LastTunnelAuthorization = request.Headers.TryGetValues("X-Tunnel-Authorization", out var values) ? values.Single() : null;
            var restore = request.RequestUri!.AbsolutePath == "/agent/persistence/restore";
            return Task.FromResult(new HttpResponseMessage(restore ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(request.RequestUri!.AbsolutePath == "/data/get" ? """{"batches":[]}""" : """{"childSessionIds":[]}"""),
            });
        }

        protected override void Dispose(bool disposing)
        {
            this.Disposed = true;
            base.Dispose(disposing);
        }
    }
}
