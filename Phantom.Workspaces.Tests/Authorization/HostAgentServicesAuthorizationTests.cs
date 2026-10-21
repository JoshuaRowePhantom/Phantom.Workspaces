using System.IO;
using AgentSchema;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Access;

namespace Phantom.Workspaces.Tests.Authorization;

public sealed class HostAgentServicesAuthorizationTests
{
    [AvaloniaTheory(Timeout = 15_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostComposition_ReplacementAndInheritedBundlesRetainExactBroker(bool runtime)
    {
        await using var host = CreateHost(new NullPlatformSecretStore());
        await using var cache = new AgentPersistenceStoreCache(host.Broker, () => host.PresentationScope);
        var applicationServices = CreateApplicationServices(host, cache);
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(
            applicationServices: applicationServices);
        await viewModel.InitializeAsync();

        var bootstrapServices = await ComposeAsync(viewModel, runtime);
        Assert.Same(host.Broker, bootstrapServices.AccessBroker);
        Assert.Same(host.Broker, (bootstrapServices with { }).AccessBroker);

        host.EnterRuntime();
        var runtimeServices = await ComposeAsync(viewModel, runtime);
        Assert.Same(host.Broker, runtimeServices.AccessBroker);
        Assert.Same(host.Broker, (runtimeServices with { }).AccessBroker);
        Assert.Equal(AccessPresentationScope.Runtime, applicationServices.AccessPresentationScope());
    }

    [AvaloniaTheory(Timeout = 15_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HostComposition_DeniedSecretInReplacementBundlePreventsRunningChat(bool runtime, bool create)
    {
        var platform = new Mock<IPlatformSecretStore>(MockBehavior.Strict);
        platform.Setup(value => value.EnumerateNamesAsync("HOST_COMPOSITION_SECRET", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "HOST_COMPOSITION_SECRET" });
        await using var host = CreateHost(platform.Object);
        await using var cache = new AgentPersistenceStoreCache(host.Broker, () => host.PresentationScope);
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(
            applicationServices: CreateApplicationServices(host, cache));
        await viewModel.InitializeAsync();
        host.EnterRuntime();
        var composed = await ComposeAsync(viewModel, runtime);
        Assert.Same(host.Broker, composed.AccessBroker);
        var supplied = composed with { ChatClientOverride = new DeterministicTestChatClient() };
        await using var factory = new AgentChatFactory(
            AgentPersistenceStoreFactory.CreateInMemory(),
            new AgentServices { AccessBroker = host.Broker },
            TaskScheduler.Default);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AuthorizationRegistry.Changed += (_, _) =>
        {
            if (host.AuthorizationRegistry.GetSnapshot().Count > 0) { registered.TrySetResult(); }
        };
        var definition = AgentDefinition.FromJson(
            """
            {
              "kind": "prompt",
              "name": "host-composition-secret",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": [{
                "kind": "mcp",
                "name": "secret-gated",
                "serverName": "secret-gated",
                "connection": {
                  "kind": "key",
                  "endpoint": "https://host-composition.invalid/",
                  "apiKey": "${SECRET:HOST_COMPOSITION_SECRET}"
                },
                "approvalMode": { "kind": "never" }
              }]
            }
            """);
        var sessionId = new AgentSessionId("host-composition-denied");
        var pending = create
            ? factory.CreateAsync(definition, sessionId, supplied, ct: TestContext.Current.CancellationToken)
            : factory.GetOrCreateAsync(sessionId, definition, supplied, ct: TestContext.Current.CancellationToken);
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var row = Assert.Single(host.AuthorizationRegistry.GetSnapshot());
        Assert.False(pending.IsCompleted);
        Assert.Empty(factory.RunningSessions);
        await host.AuthorizationRegistry.ActAsync(new DenyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
        }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SecretMaterializationRefusedException>(() => pending);
        Assert.Empty(factory.RunningSessions);
        var denied = Assert.Single(host.AuthorizationRegistry.GetSnapshot());
        Assert.Equal(row.Identifier, denied.Identifier);
        Assert.Equal(UserAuthorizationState.Denied, denied.State);
        Assert.False(denied.CanAct);
        platform.Verify(value => value.EnumerateNamesAsync(
            "HOST_COMPOSITION_SECRET", It.IsAny<CancellationToken>()), Times.Once);
        platform.VerifyNoOtherCalls();
    }

    private static AccessHostServices CreateHost(IPlatformSecretStore platform) =>
        AccessHostServices.Create(new AccessHostOptions
        {
            PlatformSecretStore = platform,
            Dispatch = action => action(),
            ReleaseMemoryFilePath = Path.Combine(Environment.CurrentDirectory,
                ".nonexistent-host-composition", Guid.NewGuid().ToString("N"), "release-memory.json"),
        });

    private static ApplicationServices CreateApplicationServices(
        AccessHostServices host, AgentPersistenceStoreCache cache) =>
        new(MainWindowIntegrationTests.CreateTestRunningAgentChatTable(), cache, NullLoggerFactory.Instance,
            accessBroker: host.Broker, accessPresentationScope: () => host.PresentationScope);

    private static Task<AgentServices> ComposeAsync(ViewModels.MainWindowViewModel viewModel, bool runtime) =>
        runtime
            ? AgentServicesComposition.ComposeRuntimeHostServicesAsync(viewModel, NullLoggerFactory.Instance)
            : AgentServicesComposition.ComposeSessionServicesAsync(
                viewModel, AgentPersistenceStoreFactory.CreateInMemory(), NullLoggerFactory.Instance);
}
