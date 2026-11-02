using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using Moq;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed partial class MainWindowTabCommandsIntegrationTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartSession_ProductionInitializationAndRestoredTab_RecomposeSlashCommandsAndDocumentNavigation(bool independentChild)
    {
        var store = new InMemoryAgentPersistenceStore();
        await using var factory = new AgentChatFactory(
            store, new AgentServices(), TaskScheduler.FromCurrentSynchronizationContext());
        var table = new RunningAgentChatTable(factory);
        var services = new ApplicationServices(
            table, new AgentPersistenceStoreCache(), NullLoggerFactory.Instance, Mock.Of<IAccessBroker>());
        await using var window = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: services);
        await window.InitializeAsync();
        Assert.NotNull(window.TransportComposition);
        Assert.IsType<AgentSessionRestartCoordinator>(window.AgentSessionRestartService);
        var owner = window.EntityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        var definitionId = new EntityId(Guid.NewGuid().ToString());
        var definition = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            window.EntityBroker, definitionId,
            $$"""
            {
              "entity-id": "{{definitionId}}",
              "entity-types": ["entity", "agent-definition"],
              "display-name": {"default":"Production acceptance"},
              "definition": {
                "kind":"prompt", "name":"production-restart",
                "model":{"id":"echo","provider":"echo","apiType":"Echo"}, "tools":[]
              }
            }
            """);
        var session = (await new AgentSessionShortcutContext().CreateAgentSessionEntityAsync(
            window, definition, "production-restart-acceptance"))!;
        await store.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = "production-restart-acceptance",
                AgentDefinitionJson = BsonDocument.Parse(definition.Data!.Value.GetProperty("definition").GetRawText()),
            },
            NewMessages =
            [
                new ChatMessage(ChatRole.User, "completed production question"),
                new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call", "tool", new Dictionary<string, object?>())]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call", "completed production result")]),
                new ChatMessage(ChatRole.Assistant, "completed production answer"),
            ],
        }, TestContext.Current.CancellationToken);
        await using var childSeed = independentChild
            ? await table.AcquireAsync(new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("production-restart-acceptance"),
                AgentSessionEntity = session.Data,
            }, TestContext.Current.CancellationToken)
            : null;
        var parentSession = independentChild
            ? await new AgentSessionShortcutContext().CreateAgentSessionEntityAsync(
                window, definition, "production-restart-parent")
            : null;
        await using var parentSeed = parentSession is not null
            ? await table.AcquireAsync(new AcquireAgentChatRequest
            {
                AgentSessionId = new AgentSessionId("production-restart-parent"),
                AgentSessionEntity = parentSession.Data,
            }, TestContext.Current.CancellationToken)
            : null;
        RemoteAgentSessionLease? parent = null;
        if (independentChild)
        {
            var host = (RemoteAgentSessionHost)typeof(RunningAgentChatTable)
                .GetField("ownerRuntimeHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(table)!;
            var runtimeFactory = (DataAccessAgentSessionRuntimeHostFactory)typeof(RemoteAgentSessionHost)
                .GetField("runtimeFactory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            parent = runtimeFactory.FindRuntime(parentSeed!.AgentChat)!;
            Assert.True(parent.TryKeepChildAlive(runtimeFactory.FindRuntime(childSeed!.AgentChat)!));
        }
        var handler = (OpenAgentSessionShortcutHandler)typeof(MainWindowViewModel)
            .GetField("openAgentSessionShortcutHandler", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
        var tab = await handler.TryCreateAgentSessionTabForRestoreAsync(new CreateAgentSessionTabForRestoreRequest
        {
            MainWindowViewModel = window,
            AgentSessionEntity = session,
            TabId = "stable-restored-tab",
        }, TestContext.Current.CancellationToken);
        Assert.NotNull(tab);
        await window.OpenTabAsync(tab);
        if (tab.State == AgentTabState.Loading)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tab.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(tab.State) && tab.State != AgentTabState.Loading)
                    ready.TrySetResult();
            };
            if (tab.State != AgentTabState.Loading)
                ready.TrySetResult();
            await ready.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(AgentTabState.Ready, tab.State);
        var original = tab.Lease!.AgentChat;
        await Assert.IsType<AgentChat>(original).HistoryPopulated;
        static string History(IAgentChat chat) => JsonSerializer.Serialize(chat.History
            .Where(item => item.Role != AgentChatHistoryItem.DiagnosticChatRole)
            .Select(item => new { item.Role, item.Contents }), AIJsonUtilities.DefaultOptions);
        var expectedHistory = History(original);
        Assert.Contains("completed production result", expectedHistory);
        var identity = tab.Lease.RestartIdentityProvider!() ?? childSeed!.RestartIdentity!;
        await using var transport = await window.TransportComposition!.TransportFactoryRegistry.ConnectToAsync(
            JsonSerializer.SerializeToElement(new { type = "local" }), TestContext.Current.CancellationToken);
        await using var remote = await RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
        {
            Client = new RemoteAgentSessionClient(transport),
            ForegroundScheduler = TaskScheduler.FromCurrentSynchronizationContext(),
            OpenRequest = new AgentSessionOpenRequest
            {
                ProtocolVersion = 1,
                AgentSessionId = identity.AgentSessionId,
                ExpectedOwningProfileEntityId = identity.OwningProfileEntityId,
                ExpectedOwnershipGeneration = identity.OwnershipGeneration,
                OpenIntent = AgentSessionOpenIntent.Attach,
                AttachmentToken = Guid.NewGuid().ToString("N"),
                Capabilities = [],
            },
        }, TestContext.Current.CancellationToken);
        if (independentChild)
        {
            Assert.Null(tab.Lease.RestartIdentityProvider());
            Assert.Null(remote.RestartClient.RestartIdentity);
            Assert.NotNull(remote.RestartClient.RestartDisabledReason);
            var capabilityRecovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            remote.RuntimeStateChanged += (_, _) =>
            {
                if (remote.RestartClient.RestartIdentity is not null)
                    capabilityRecovered.TrySetResult();
            };
            await parent!.DisposeAsync();
            await capabilityRecovered.Task.WaitAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(identity, tab.Lease.RestartIdentityProvider());
        Assert.Equal(identity, remote.RestartClient.RestartIdentity);
        Assert.Null(remote.RestartClient.RestartDisabledReason);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reprojected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.RestartClient.UnexpectedlyDisconnected += (_, _) => disconnected.TrySetResult();
        remote.InformationChanged += (_, _) =>
        {
            if (remote.RestartClient.RestartIdentity?.RuntimeEpoch != identity.RuntimeEpoch)
                reprojected.TrySetResult();
        };

        var result = await tab.Lease.RestartRuntimeAsync!(new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = identity,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, result.State);
        Assert.NotSame(original, tab.Lease!.AgentChat);
        Assert.Equal("stable-restored-tab", tab.Id);
        Assert.Equal("production-restart-acceptance", tab.Lease.SessionId.Value);
        Assert.Equal(expectedHistory, History(tab.Lease.AgentChat));
        await disconnected.Task.WaitAsync(TestContext.Current.CancellationToken);
        await remote.ReconnectNowAsync(TestContext.Current.CancellationToken);
        await reprojected.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(expectedHistory, History(remote));
        Assert.Equal(result.ReplacementRuntime, remote.RestartClient.RestartIdentity);
        Assert.NotNull(tab.Agent!.InputQueue!.DefaultComposer.SlashCommandInterceptorAsync);
        await tab.Agent.InputQueue.DefaultComposer.SlashCommandInterceptorAsync!("/title After restart");
        Assert.Equal("After restart", tab.Title);
        await tab.Agent.InputQueue.DefaultComposer.SlashCommandInterceptorAsync!("/rename Renamed after restart");
        Assert.Equal("Renamed after restart", Assert.Single(table.RunningSessions).SessionTitle);
        await tab.Agent.OpenDocumentLinkAsync("https://example.invalid/restart-navigation");
        Assert.Contains(window.SelectedWorkspacePane!.Tabs,
            item => item is WebViewModel && item.Title.Contains("example.invalid/restart-navigation", StringComparison.Ordinal));
    }
}
