using System.Threading.Tasks;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Moq;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Secrets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace Phantom.Workspaces.Tests;

// Issue #1403: AgentServicesComposition is the single composition root that produces the complete
// AgentServices bundle for a launch. This test proves the bundle carries every service every launch
// path needs, so no path can silently drop one (the root cause of #1401 and #1402).
public sealed class AgentServicesCompositionTests
{
    [Fact]
    public void ComposeHostServicesRejectsMissingOwningBroker()
    {
        Assert.Throws<ArgumentNullException>(() => AgentServicesComposition.ComposeHostServices(
            null!, NullLoggerFactory.Instance));
    }

    private static ApplicationServices CreateApplicationServices(
        IMcpOAuthCredentialRequestCatalog mcpOAuthCredentials,
        Func<AccessPresentationScope>? accessPresentationScopeProvider = null,
        IRemoteCopilotProviderCatalog? remoteCopilotProviderCatalog = null)
        => new(
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable(),
            new AgentPersistenceStoreCache(),
            NullLoggerFactory.Instance,
            accessBroker: Moq.Mock.Of<Phantom.Workspaces.Llm.Interfaces.Access.IAccessBroker>(),
            credentialPicker: new NullCredentialPicker(),
            allowedSecretsStore: new AllowedSecretsStore(new AllowedSecretsStoreConfiguration()),
            platformSecretStore: new NullPlatformSecretStore(),
            mcpOAuthCredentials: mcpOAuthCredentials,
            accessPresentationScope: accessPresentationScopeProvider)
        {
            RemoteCopilotProviderCatalog = remoteCopilotProviderCatalog,
        };

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentServicesComposition_Compose_ProducesCompleteBundle()
    {
        var mcpOAuthCredentials = Mock.Of<IMcpOAuthCredentialRequestCatalog>();
        var phase = AccessPresentationScope.Bootstrap;
        Func<AccessPresentationScope> presentationProvider = () => phase;
        var catalog = new EnvironmentRemoteCopilotProviderCatalog();
        var applicationServices = CreateApplicationServices(mcpOAuthCredentials, presentationProvider, catalog);
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(
            applicationServices: applicationServices);
        await viewModel.InitializeAsync();

        var services = await AgentServicesComposition.ComposeSessionServicesAsync(
            viewModel,
            AgentPersistenceStoreFactory.CreateInMemory(),
            NullLoggerFactory.Instance);

        // Every service the launch paths depend on is present in the one bundle.
        Assert.NotNull(services.AccessBroker);
        Assert.Same(applicationServices.AccessBroker, services.AccessBroker);
        Assert.Same(mcpOAuthCredentials, services.McpOAuthCredentials);
        Assert.Same(presentationProvider, services.AccessPresentationScopeProvider);
        Assert.Equal(AccessPresentationScope.Bootstrap, services.AccessPresentationScope);
        phase = AccessPresentationScope.Runtime;
        Assert.Equal(AccessPresentationScope.Runtime, services.AccessPresentationScope);
        Assert.NotNull(services.ToolsetFactory);
        Assert.NotNull(services.ToolResourceFactory);
        Assert.NotNull(services.AccountUpsertService);
        Assert.NotNull(services.CurrentSessionContext);
        Assert.NotNull(services.AgentPersistenceStoreOverride);
        Assert.NotNull(services.ProcessExecutor);
        Assert.NotNull(services.TrustProfilePolicyCompiler);
        Assert.Same(catalog, services.RemoteCopilotProviderCatalog);
        Assert.Same(catalog, services.GetService(typeof(IRemoteCopilotProviderCatalog)));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentServicesComposition_ComposeHostServices_CarriesTypedBrokerAndMcpCatalog()
    {
        var accessBroker = Mock.Of<IAccessBroker>();
        var mcpOAuthCredentials = Mock.Of<IMcpOAuthCredentialRequestCatalog>();
        var catalog = new EnvironmentRemoteCopilotProviderCatalog();

        var services = AgentServicesComposition.ComposeHostServices(
            accessBroker,
            NullLoggerFactory.Instance,
            mcpOAuthCredentials: mcpOAuthCredentials,
            remoteCopilotProviderCatalog: catalog);

        Assert.Same(accessBroker, services.AccessBroker);
        Assert.Same(mcpOAuthCredentials, services.McpOAuthCredentials);
        Assert.NotNull(services.ProcessExecutor);
        Assert.NotNull(services.TrustProfilePolicyCompiler);
        Assert.Same(catalog, services.RemoteCopilotProviderCatalog);
        Assert.Same(catalog, services.GetService(typeof(IRemoteCopilotProviderCatalog)));
        await Task.CompletedTask;
    }

    [Fact]
    public void AgentServicesComposition_UnconfiguredCatalog_RemainsUnavailable()
    {
        var services = AgentServicesComposition.ComposeHostServices(Mock.Of<IAccessBroker>(), NullLoggerFactory.Instance);
        Assert.Null(services.RemoteCopilotProviderCatalog);
        Assert.Null(services.GetService(typeof(IRemoteCopilotProviderCatalog)));
        Assert.Null(services.GetService(typeof(object)));
    }

    [Fact]
    public void AgentServicesComposition_RejectsMissingHostBroker()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AgentServicesComposition.ComposeHostServices(null!, NullLoggerFactory.Instance));
    }

    [Fact]
    public void AgentServicesComposition_ComposeHostServices_CarriesProcessLoggerFactory()
    {
        using var process = LoggerFactory.Create(builder => builder.AddProvider(new TestLoggerProvider()));
        var services = AgentServicesComposition.ComposeHostServices(Mock.Of<IAccessBroker>(), process);
        Assert.Same(process, services.LoggerFactory);
    }

    [Fact]
    public void AgentServicesComposition_PresentationProvider_IsSharedWithoutFreezingHostPhase()
    {
        var phase = AccessPresentationScope.Bootstrap;
        Func<AccessPresentationScope> provider = () => phase;
        var services = AgentServicesComposition.ComposeHostServices(
            Mock.Of<IAccessBroker>(),
            NullLoggerFactory.Instance,
            accessPresentationScopeProvider: provider);
        Assert.Same(provider, services.AccessPresentationScopeProvider);
        Assert.Equal(AccessPresentationScope.Bootstrap, services.AccessPresentationScope);
        var session = services with { LogChat = true };
        phase = AccessPresentationScope.Runtime;
        Assert.Same(provider, session.AccessPresentationScopeProvider);
        Assert.Equal(AccessPresentationScope.Runtime, session.AccessPresentationScope);
        Assert.Equal(AccessPresentationScope.Runtime, new AgentServices().AccessPresentationScope);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentServicesComposition_ComposeRuntimeHostServices_CarriesProcessLoggerFactory()
    {
        using var process = LoggerFactory.Create(builder => builder.AddProvider(new TestLoggerProvider()));
        var applicationServices = CreateApplicationServices(Mock.Of<IMcpOAuthCredentialRequestCatalog>());
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(
            applicationServices: applicationServices);
        await viewModel.InitializeAsync();
        var services = await AgentServicesComposition.ComposeRuntimeHostServicesAsync(viewModel, process);
        Assert.Same(process, services.LoggerFactory);
    }

    private sealed class TestLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new TestLogger();
        public void Dispose() { }
    }

    private sealed class TestLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _ = formatter(state, exception);
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentServicesComposition_ComposeRuntimeHostServices_CarriesRuntimeToolsWithoutReplacingPersistence()
    {
        var phase = AccessPresentationScope.Bootstrap;
        Func<AccessPresentationScope> presentationProvider = () => phase;
        var catalog = new EnvironmentRemoteCopilotProviderCatalog();
        var applicationServices = CreateApplicationServices(
            Mock.Of<IMcpOAuthCredentialRequestCatalog>(),
            presentationProvider,
            catalog);
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(
            applicationServices: applicationServices);
        await viewModel.InitializeAsync();

        var services = await AgentServicesComposition.ComposeRuntimeHostServicesAsync(
            viewModel,
            NullLoggerFactory.Instance);

        Assert.Null(services.AgentPersistenceStoreOverride);
        Assert.Same(applicationServices.AccessBroker, services.AccessBroker);
        Assert.Same(applicationServices.McpOAuthCredentials, services.McpOAuthCredentials);
        Assert.Same(catalog, services.RemoteCopilotProviderCatalog);
        Assert.Same(catalog, services.GetService(typeof(IRemoteCopilotProviderCatalog)));
        Assert.Same(presentationProvider, services.AccessPresentationScopeProvider);
        Assert.Equal(AccessPresentationScope.Bootstrap, services.AccessPresentationScope);
        phase = AccessPresentationScope.Runtime;
        Assert.Equal(AccessPresentationScope.Runtime, services.AccessPresentationScope);
        Assert.NotNull(services.ToolsetFactory);
        Assert.NotNull(services.ToolResourceFactory);
        Assert.NotNull(services.CurrentSessionContext);
        Assert.NotNull(services.AccountUpsertService);
        Assert.Same(NullLoggerFactory.Instance, services.LoggerFactory);
    }

    [Fact]
    public async Task DataAccessLayerTrustProfileResolver_ReturnsComposedProfileAndConcurrencyRevision()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "names": [["trust-profiles", "restricted"]],
              "hosting-workspaces-client-instances": ["."],
              "network-capabilities": []
            }
            """);
        var snapshot = new QueryEntitySnapshot
        {
            EntityId = new EntityId("11111111-1111-1111-1111-111111111111"),
            ConcurrencyTag = new ConcurrencyTag("revision-7"),
            ModifiedTime = new Timestamp(DateTimeOffset.UnixEpoch, "change-1"),
            Data = document.RootElement.Clone(),
            Relationships = [],
            MatchingClauseIdentifiers = [],
        };
        var dataAccessLayer = new Mock<IDataAccessLayer>();
        dataAccessLayer
            .Setup(layer => layer.QueryAsync(It.IsAny<QueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueryResult
            {
                Batches =
                [
                    new TimestampedQueryBatch
                    {
                        Timestamp = snapshot.ModifiedTime,
                        Entities = [snapshot],
                    },
                ],
            });
        var resolver = new DataAccessLayerTrustProfileResolver(dataAccessLayer.Object);

        var resolved = await resolver.ResolveVersionedAsync(
            "restricted",
            TestContext.Current.CancellationToken);
        var remote = await ((IRemoteTrustProfileResolver)resolver)
            .ResolveAsync("restricted", TestContext.Current.CancellationToken);

        Assert.Equal("revision-7", resolved.Revision);
        Assert.NotNull(resolved.Profile.NetworkCapabilities);
        Assert.Equal("revision-7", remote!.Revision);
    }
}
