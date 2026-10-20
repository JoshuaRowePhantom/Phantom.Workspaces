using System.IO;
using System.Reflection;
using Moq;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Tools;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests.Access;

public sealed class AccessHostServicesTests : IDisposable
{
    private readonly string directory = Path.Combine(Environment.CurrentDirectory, ".access-host-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }

    [Fact]
    public void AccessHostServices_Create_PartialRowsConstructionDoesNotLeaveLateProjection()
    {
        Action? queued = null;
        Assert.Throws<InvalidOperationException>(() => AccessHostServices.Create(new AccessHostOptions
        {
            PlatformSecretStore = new NullPlatformSecretStore(),
            ReleaseMemoryFilePath = Path.Combine(this.directory, "release-memory.json"),
            Dispatch = action =>
            {
                queued = action;
                throw new InvalidOperationException("Dispatcher unavailable.");
            },
        }));
        Assert.NotNull(queued);
        queued();
    }

    [Fact]
    public async Task AccessHostServices_Create_RegistersEachClosedExecutorExactlyOnce()
    {
        await using var host = this.Create(new NullPlatformSecretStore());
        var registry = typeof(AccessBroker).GetField("executors", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host.Broker)!;
        var registrations = Assert.IsAssignableFrom<System.Collections.IDictionary>(
            registry.GetType().GetField("registrations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(registry));
        var requests = registrations.Keys.Cast<Type>().ToArray();
        Assert.Equal(requests.Length, requests.Distinct().Count());
        Assert.Contains(typeof(StoredSecretAccessRequest), requests);
        Assert.Contains(typeof(GitHubCredentialAccessRequest), requests);
        Assert.Contains(typeof(EntraCredentialAccessRequest), requests);
        Assert.Contains(typeof(DevTunnelIdentityCredentialAccessRequest), requests);
        Assert.Contains(typeof(DevTunnelConnectCredentialAccessRequest), requests);
        Assert.Contains(typeof(CliOwnedAuthorizationRequest), requests);
        Assert.All(requests, request => Assert.True(request.IsSealed));
    }

    [Fact]
    public async Task AccessHostServices_Create_ProvidesOneBrokerRegistryAndRowsGraph()
    {
        var platform = new Mock<IPlatformSecretStore>(MockBehavior.Strict);
        platform.Setup(value => value.EnumerateNamesAsync("TEST_SECRET", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "TEST_SECRET" });
        await using var host = this.Create(platform.Object);
        var template = StoredSecretAccessExecutorTests.Request();
        var request = template with
        {
            PresentationScope = AccessPresentationScope.Bootstrap,
            Sources = template.Sources.Where(source => source.Kind == CredentialSourceKind.SecretStore).ToArray(),
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = host.Broker.AcquireAsync(request, cancellation.Token);

        var row = Assert.Single(host.AuthorizationRegistry.GetSnapshot());
        var presented = Assert.Single(host.AuthorizationRows.Rows);
        host.AuthorizationRows.SetPresentationMode(AuthorizationRowsPresentationMode.Runtime);
        Assert.Same(presented, Assert.Single(host.AuthorizationRows.Rows));
        Assert.Equal(row.Identifier, presented.Identifier);
        Assert.False(pending.IsCompleted);
        platform.Verify(value => value.EnumerateNamesAsync("TEST_SECRET", It.IsAny<CancellationToken>()), Times.Once);
        platform.VerifyNoOtherCalls();
        await cancellation.CancelAsync();
        Assert.IsType<AccessCancelled<StoredSecretAccessResult>>(await pending);
    }

    [Fact]
    public async Task AccessHostServices_DisposeAsync_CancelsPendingRowsAndRejectsNewUse()
    {
        var platform = new Mock<IPlatformSecretStore>();
        platform.Setup(value => value.EnumerateNamesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "TEST_SECRET" });
        var host = this.Create(platform.Object);
        var request = StoredSecretAccessExecutorTests.Request() with { PresentationScope = AccessPresentationScope.Bootstrap };
        var pending = host.Broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        Assert.NotEmpty(host.AuthorizationRegistry.GetSnapshot());

        await host.DisposeAsync();
        await host.DisposeAsync();

        Assert.IsType<AccessShuttingDown<StoredSecretAccessResult>>(await pending);
        Assert.IsType<AccessShuttingDown<StoredSecretAccessResult>>(
            await host.Broker.AcquireAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(host.AuthorizationRegistry.GetSnapshot());
        Assert.Empty(host.AuthorizationRows.Rows);
    }

    private AccessHostServices Create(IPlatformSecretStore store) => AccessHostServices.Create(new AccessHostOptions
    {
        PlatformSecretStore = store,
        Dispatch = action => action(),
        ReleaseMemoryFilePath = Path.Combine(this.directory, "release-memory.json"),
    });
}
