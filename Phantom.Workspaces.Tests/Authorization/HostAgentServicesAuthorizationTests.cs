using System.IO;
using AgentSchema;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.AI;
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

public sealed class HostAgentServicesAuthorizationTests(ITestOutputHelper output)
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
    public Task HostComposition_DeniedOptionalMcpSecretInReplacementBundleKeepsRunningChat(
        bool runtime,
        bool create) =>
        VerifyDeniedSecretAsync(runtime, create, optionalServer: true);

    [AvaloniaTheory(Timeout = 15_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task HostComposition_DeniedRequiredCoreSecretInReplacementBundlePreventsRunningChat(
        bool runtime,
        bool create) =>
        VerifyDeniedSecretAsync(runtime, create, optionalServer: false);

    private async Task VerifyDeniedSecretAsync(
        bool runtime,
        bool create,
        bool optionalServer)
    {
        var platform = new Mock<IPlatformSecretStore>(MockBehavior.Strict);
        platform.Setup(value => value.EnumerateNamesAsync("HOST_COMPOSITION_SECRET", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "HOST_COMPOSITION_SECRET" });
        await using var host = CreateHost(platform.Object);
        await using var cache = new AgentPersistenceStoreCache(host.Broker, () => host.PresentationScope);
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(
            applicationServices: CreateApplicationServices(host, cache));
        await viewModel.InitializeAsync();
        output.WriteLine("Host view model initialized.");
        host.EnterRuntime();
        var composed = await ComposeAsync(viewModel, runtime);
        Assert.Same(host.Broker, composed.AccessBroker);
        var client = new DeterministicTestChatClient();
        var transportCreations = 0;
        var supplied = composed with
        {
            ChatClientOverride = client,
            McpHttpMessageHandlerFactory = () =>
            {
                Interlocked.Increment(ref transportCreations);
                throw new InvalidOperationException("Unauthorized MCP transport creation.");
            },
        };
        Assert.Same(host.Broker, supplied.AccessBroker);
        await using var factory = new AgentChatFactory(
            AgentPersistenceStoreFactory.CreateInMemory(),
            new AgentServices { AccessBroker = host.Broker },
            TaskScheduler.Default);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.AuthorizationRegistry.Changed += (_, _) =>
        {
            if (host.AuthorizationRegistry.GetSnapshot().Count > 0) { registered.TrySetResult(); }
        };
        var definition = AgentDefinition.FromJson(optionalServer
            ?
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
            """
            :
            """
            {
              "kind": "prompt",
              "name": "host-composition-required-secret",
              "model": {
                "id": "echo",
                "provider": "echo",
                "apiType": "Echo",
                "connection": { "kind": "key", "apiKey": "${SECRET:HOST_COMPOSITION_SECRET}" }
              }
            }
            """);
        var sessionId = new AgentSessionId("host-composition-denied");
        var pending = create
            ? factory.CreateAsync(definition, sessionId, supplied, ct: TestContext.Current.CancellationToken)
            : factory.GetOrCreateAsync(sessionId, definition, supplied, ct: TestContext.Current.CancellationToken);
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        output.WriteLine("Credential authorization registered.");
        var row = Assert.Single(host.AuthorizationRegistry.GetSnapshot());
        Assert.Equal(UserAuthorizationState.Pending, row.State);
        Assert.False(row.IsBootstrapBlocking);
        Assert.Contains(row.MemoryOptions, option => option.Scope == ReleaseMemoryScopeKind.SessionIdentity);
        Assert.Contains(row.MemoryOptions, option => option.Scope == ReleaseMemoryScopeKind.KeyInSession);
        Assert.Equal(optionalServer ? "Connect secret-gated" : "Use manifest secret", row.UseDisplayName);
        await using var lease = optionalServer
            ? await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            : null;
        var chat = lease?.LocalAgentChat;
        if (optionalServer)
        {
            Assert.Single(factory.RunningSessions);
            await chat!.HistoryPopulated;
            Assert.False(chat.Initialization.IsCompleted);
            var server = Assert.Single(chat.GetToolSnapshot());
            Assert.Equal("secret-gated", server.Name);
            Assert.Contains("authorization", server.Status, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(server.Children);
            await AssertAnswerAsync(chat, client);
            output.WriteLine("Core turn settled while optional authorization is pending.");
            Assert.Contains("secret-gated", client.LastRequestOptions!.Instructions, StringComparison.Ordinal);
            Assert.Contains("authorization", client.LastRequestOptions.Instructions, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(UserAuthorizationState.Pending, Assert.Single(host.AuthorizationRegistry.GetSnapshot()).State);
        }
        else
        {
            Assert.False(pending.IsCompleted);
            Assert.Empty(factory.RunningSessions);
            Assert.Empty(client.LastRequestMessages);
        }
        Assert.Equal(0, Volatile.Read(ref transportCreations));
        await host.AuthorizationRegistry.ActAsync(new DenyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
        }, TestContext.Current.CancellationToken);
        output.WriteLine("Authorization denied.");

        if (optionalServer)
        {
            await chat!.Initialization.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var server = Assert.Single(chat.GetToolSnapshot());
            Assert.Equal("secret-gated", server.Name);
            Assert.False(server.IsEnabled);
            Assert.Empty(server.Children);
            Assert.Contains("Failed to open MCP server", server.Status, StringComparison.Ordinal);
            Assert.Single(factory.RunningSessions);
            await AssertAnswerAsync(chat, client);
            output.WriteLine("Core turn settled after optional server denial.");
            Assert.Contains("secret-gated", client.LastRequestOptions!.Instructions, StringComparison.Ordinal);
        }
        else
        {
            await Assert.ThrowsAsync<SecretMaterializationRefusedException>(() => pending);
            Assert.Empty(factory.RunningSessions);
            Assert.Empty(client.LastRequestMessages);
        }
        Assert.Equal(0, Volatile.Read(ref transportCreations));
        var denied = Assert.Single(host.AuthorizationRegistry.GetSnapshot());
        Assert.Equal(row.Identifier, denied.Identifier);
        Assert.Equal(UserAuthorizationState.Denied, denied.State);
        Assert.False(denied.CanAct);
        platform.Verify(value => value.EnumerateNamesAsync(
            "HOST_COMPOSITION_SECRET", It.IsAny<CancellationToken>()), Times.Once);
        platform.VerifyNoOtherCalls();
        output.WriteLine("Denial assertions complete; disposing session and host.");
    }

    private static async Task AssertAnswerAsync(
        AgentChat chat,
        DeterministicTestChatClient client)
    {
        var response = client.EnqueueStreamingResponse();
        response.EnqueueUpdate(new ChatResponseUpdate(ChatRole.Assistant, "Core answer.")
        {
            FinishReason = ChatFinishReason.Stop,
        });
        response.Complete();
        var completion = chat.EnqueueUserMessageWithCompletion("Please answer.");
        await completion.Settlement.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains(chat.History,
            item => item.Role == ChatRole.Assistant
                && item.Contents.OfType<TextContent>().Any(content => content.Text == "Core answer."));
        Assert.Empty(client.LastRequestOptions!.Tools ?? []);
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
