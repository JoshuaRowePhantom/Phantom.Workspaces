using System.IO;
using Moq;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Llm.Interfaces.Access;
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
        var registry = new UserAuthorizationRegistry();
        var executors = new CredentialAccessExecutorRegistry();
        var mint = new Mock<IDevTunnelConnectMintSession>(MockBehavior.Strict);
        mint.Setup(session => session.DisposeAsync()).Returns(ValueTask.CompletedTask);
        executors.Add<DevTunnelConnectCredentialAccessRequest, DevTunnelConnectCredentialAccessResult>(
            new DevTunnelConnectCredentialAccessExecutor(mint.Object, registry));
        await using var broker = new AccessBroker(
            new ReleaseAuthorizationPolicy(new ReleaseMemoryStore(Path.Combine(
                Environment.CurrentDirectory, ".named-anonymous-tests", Guid.NewGuid().ToString("N"), "memory.json"))),
            executors,
            new ProviderAcquisitionCoordinator(),
            registry);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task operation = persistence
            ? new AgentPersistenceStoreSourceFactory(source, broker, null, () => AccessPresentationScope.Bootstrap)
                .CreateAsync(cancellation.Token)
            : CreateRepositoryAsync();
        try
        {
            var row = await PendingAsync(registry, operation);
            Assert.True(row.IsBootstrapBlocking);
            Assert.Contains("Management", row.UseDisplayName, StringComparison.OrdinalIgnoreCase);
            Assert.False(operation.IsCompleted);
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

        async Task<object> CreateRepositoryAsync() =>
            await EntityRepository.CreateDevTunnelNameDataAccessLayerAsync(
                source, broker, null, () => AccessPresentationScope.Bootstrap, cancellation.Token);
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
}
