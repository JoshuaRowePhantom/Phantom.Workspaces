using System.IO;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Tests.Access;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests.Authorization;

public sealed class AuthorizationEndToEndTests
{
    [AvaloniaFact]
    public async Task LoadingToRuntime_PendingAuthorizationRetainsExactRowsBrokerAndRequest()
    {
        var platform = new Mock<IPlatformSecretStore>();
        platform.Setup(value => value.EnumerateNamesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "TEST_SECRET" });
        await using var host = App.CreateAccessHostBeforeStartup(new AccessHostOptions
        {
            PlatformSecretStore = platform.Object,
            Dispatch = action => action(),
            ReleaseMemoryFilePath = Path.Combine(Environment.CurrentDirectory, ".nonexistent-startup-authorization", "release-memory.json"),
        });
        var loading = new LoadingWindowViewModel { AuthorizationRows = host.AuthorizationRows };
        var request = StoredSecretAccessExecutorTests.Request() with { PresentationScope = AccessPresentationScope.Bootstrap };
        var pending = host.Broker.AcquireAsync(request, TestContext.Current.CancellationToken);
        var row = Assert.Single(loading.AuthorizationRows.Rows);
        await using var cache = new AgentPersistenceStoreCache(host.Broker, () => host.PresentationScope);
        var services = new ApplicationServices(
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable(), cache, NullLoggerFactory.Instance,
            accessBroker: host.Broker, accessPresentationScope: () => host.PresentationScope);
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: services);
        await viewModel.InitializeAsync();
        await using var popup = new AuthorizationRequestsPopupViewModel { Rows = host.AuthorizationRows };
        var window = new MainWindow(viewModel);
        window.AttachAccessHost(host, popup);

        Assert.Same(loading.AuthorizationRows, popup.Rows);
        Assert.Same(row, Assert.Single(popup.Rows.Rows));
        Assert.Same(host.Broker, viewModel.ApplicationServices.AccessBroker);
        Assert.Same(host.Broker, viewModel.EntityBroker.EntityRepository.AccessBroker);
        Assert.Single(host.AuthorizationRegistry.GetSnapshot());
        Assert.False(pending.IsCompleted);
        Assert.Equal(AccessPresentationScope.Runtime, services.AccessPresentationScope());

        await host.DisposeAsync();
        Assert.IsType<AccessShuttingDown<StoredSecretAccessResult>>(await pending);
        Assert.Empty(popup.Rows.Rows);
    }

    [AvaloniaFact]
    public async Task AuthorizationEndToEnd_PendingRepositoryBootstrap_ShutdownDoesNotContactProviderOrRecreateRows()
    {
        await using var host = App.CreateAccessHostBeforeStartup(new AccessHostOptions
        {
            PlatformSecretStore = new NullPlatformSecretStore(),
            Dispatch = action => action(),
            ReleaseMemoryFilePath = Path.Combine(Environment.CurrentDirectory, ".nonexistent-startup-repository", "release-memory.json"),
        });
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AuthorizationRegistry.Changed += (_, _) =>
        {
            if (host.AuthorizationRegistry.GetSnapshot().Count > 0) { registered.TrySetResult(); }
        };
        await using var cache = new AgentPersistenceStoreCache(host.Broker, () => host.PresentationScope);
        var services = new ApplicationServices(
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable(), cache, NullLoggerFactory.Instance,
            accessBroker: host.Broker, accessPresentationScope: () => host.PresentationScope);
        var viewModel = new MainWindowViewModel(
            new WebRepositorySource("https://repository.invalid/", UseGitHubAuthToken: true),
            new Configuration.WorkspacesConfiguration { SkipStartupWorkspace = true },
            new ProfileStore(Path.Combine(Environment.CurrentDirectory, ".nonexistent-startup-repository", "profile.json")),
            services);
        var initialization = viewModel.InitializeAsync();
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Single(host.AuthorizationRows.Rows);
        Assert.False(initialization.IsCompleted);

        await host.DisposeAsync();
        await viewModel.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);

        Assert.Empty(host.AuthorizationRows.Rows);
        Assert.Empty(host.AuthorizationRegistry.GetSnapshot());
        await viewModel.DisposeAsync();
    }

    [AvaloniaFact]
    public async Task AuthorizationEndToEnd_PendingPersistenceBootstrap_ShutdownCancelsWithoutProviderContact()
    {
        await using var host = App.CreateAccessHostBeforeStartup(new AccessHostOptions
        {
            PlatformSecretStore = new NullPlatformSecretStore(),
            Dispatch = action => action(),
            ReleaseMemoryFilePath = Path.Combine(Environment.CurrentDirectory, ".nonexistent-startup-persistence", "release-memory.json"),
        });
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AuthorizationRegistry.Changed += (_, _) =>
        {
            if (host.AuthorizationRegistry.GetSnapshot().Count > 0) { registered.TrySetResult(); }
        };
        await using var cache = new AgentPersistenceStoreCache(host.Broker, () => host.PresentationScope);
        var store = await cache.GetOrCreateAsync(
            new WebRepositorySource("https://persistence.invalid/", UseGitHubAuthToken: true));
        var pending = store.ReadSubAgentChildIdsAsync("session", TestContext.Current.CancellationToken).AsTask();
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Single(host.AuthorizationRows.Rows);
        Assert.False(pending.IsCompleted);

        await host.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Empty(host.AuthorizationRegistry.GetSnapshot());
        Assert.Empty(host.AuthorizationRows.Rows);
    }

    [AvaloniaFact]
    public async Task MainWindowViewModel_DisposeAsync_IsExactlyOnceAndPreventsLateInitialization()
    {
        var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        var initialization = viewModel.InitializeAsync();
        Assert.Same(initialization, viewModel.InitializeAsync());
        await initialization;
        await viewModel.DisposeAsync();
        await viewModel.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => viewModel.InitializeAsync());
    }
}
