using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Gui.Shared.Utilities;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Access.Execution;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests.Mcp;

public sealed partial class McpOAuthRedirectHandlerTests
{
    [AvaloniaTheory(Timeout = 30_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SessionAcquisition_DelayedLoopbackOAuth_OpenAndCreatePreserveSharedParticipant(
        bool create, bool cancelStartup)
    {
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new AttemptOperation();
        var browser = new FakeSystemBrowserLauncher { OnOpenSideEffect = () => launched.TrySetResult() };
        await using var attempt = await BoundAttemptAsync(browser, AuthUri("private-state"), operation);
        await using var coordinator = new ProviderAcquisitionCoordinator();
        operation.Execute = async (context, cancellation) =>
        {
            var callback = await attempt.ExecuteAsync(context.InteractionPermit, cancellation);
            Assert.Equal("private-code", GetQueryValue(callback, "code"));
            return await context.CommitAsync(new() { Value = Guid.NewGuid() }, _ => ValueTask.CompletedTask);
        };
        var acquisitionFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new OAuthAcquisitionStore(async cancellation =>
        {
            try
            {
                Assert.IsType<ProviderAcquisitionSucceeded>(await coordinator.ExecuteOrJoinAsync(
                    operation, AttemptPermit(operation), cancellation));
            }
            finally { acquisitionFinished.TrySetResult(); }
        });
        await using var factory = new AgentChatFactory(store, new AgentServices(),
            TaskScheduler.FromCurrentSynchronizationContext());
        var table = new RunningAgentChatTable(factory);
        var cache = new Mock<IAgentPersistenceStoreCache>();
        cache.Setup(value => value.GetOrCreateAsync(It.IsAny<RepositorySource>()))
            .ReturnsAsync(store);
        var services = new ApplicationServices(table, cache.Object, NullLoggerFactory.Instance, Mock.Of<IAccessBroker>());
        await using var main = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: services);
        await main.InitializeAsync();
        var sourceId = new EntityId(Guid.NewGuid());
        var source = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            MainWindowIntegrationTests.GetEntityBroker(main), sourceId,
            $$$"""
            {"entity-id":"{{{sourceId}}}","entity-types":["entity","agent-definition"],
             "names":[["tests","oauth-acquisition","{{{sourceId}}}"]],"display-name":{"default":"OAuth acquisition"},
             "definition":{"kind":"prompt","name":"oauth-acquisition",
             "model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}}
            """);
        var context = new AgentSessionShortcutContext(persistenceStoreCache: cache.Object);
        var clock = new FakeTimeProvider();
        await using var handler = new OpenAgentSessionShortcutHandler(context,
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(), table,
            new AgentSessionOwnerDecisionProvider(), timeProvider: clock, stageDeadline: TimeSpan.FromSeconds(5));
        await using var lifetime = new ViewModelLifetime();
        if (create)
        {
            Assert.NotNull(await AgentManifestSessionLauncher.LaunchAsync(
                lifetime, main, context, handler, source, null, null));
        }
        else
        {
            var entity = await context.CreateAgentSessionEntityAsync(main, source, Guid.NewGuid().ToString("N"));
            Assert.NotNull(entity);
            Assert.True(await handler.Handle(main, Shortcut.Open, entity));
        }
        await launched.Task.WaitAsync(TestContext.Current.CancellationToken);
        var tab = Assert.Single(main.WorkspacePanes.SelectMany(pane => pane.Tabs).OfType<AgentSessionWorkspaceTabViewModel>());
        var constructing = Assert.IsType<AgentChat>(tab.StartupProgress.ObservedChat);
        var diagnostic = Assert.Single(constructing.RunningItems);
        var text = Assert.IsType<TextContent>(Assert.Single(Assert.Single(diagnostic.Items).Contents)).Text!;
        Assert.Contains("Waiting for your authorization", text);
        Assert.DoesNotContain("private-state", text);
        var actions = Regex.Matches(text, @"phantom-startup:[a-f0-9]+");
        Assert.Equal(2, actions.Count);
        var shared = coordinator.ExecuteOrJoinAsync(operation, AttemptPermit(operation), TestContext.Current.CancellationToken).AsTask();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(AgentTabState.Loading, tab.State);
        Assert.Null(tab.Lease);
        Assert.False(shared.IsCompleted);
        Assert.True(attempt.IsListenerBound);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(tab.State) && tab.State == AgentTabState.Ready) ready.TrySetResult();
        };
        if (cancelStartup)
        {
            await AgentStartupActions.TryInvokeAsync(constructing, actions[1].Value);
            await acquisitionFinished.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(tab.StartupCancellation.IsCancellationRequested);
            Assert.Equal(AgentTabState.Failed, tab.State);
            Assert.Contains(tab, main.WorkspacePanes.SelectMany(pane => pane.Tabs));
            Assert.Null(tab.Lease);
            Assert.False(shared.IsCompleted);
            Assert.True(attempt.IsListenerBound);
        }
        else
        {
            await AgentStartupActions.TryInvokeAsync(constructing, actions[0].Value);
            Assert.Equal(2, browser.OpenCount);
        }
        await SendCallbackAsync(operation.RedirectUri, "code=private-code&state=private-state");
        Assert.IsType<ProviderAcquisitionSucceeded>(await shared);
        Assert.False(attempt.IsListenerBound);
        if (cancelStartup)
        {
            await lifetime.DisposeAsync();
            await handler.DisposeAsync();
            Assert.False(ready.Task.IsCompleted);
            Assert.Null(tab.Lease);
            Assert.Empty(factory.RunningSessions);
        }
        else
        {
            await ready.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(tab.Lease);
            Assert.Same(constructing, tab.Agent!.AgentChat);
            Assert.Empty(constructing.RunningItems);
        }
        await AgentStartupActions.TryInvokeAsync(constructing, actions[0].Value);
        Assert.Equal(cancelStartup ? 1 : 2, browser.OpenCount);
    }

    // The persistence boundary requires real provider authorization, not a manually gated
    // launcher delegate. Everything above it is the public open/create and table/factory path.
    private sealed class OAuthAcquisitionStore(Func<CancellationToken, Task> authorize) : IAgentPersistenceStore
    {
        private readonly InMemoryAgentPersistenceStore inner = new();
        public ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
            => this.inner.StoreAsync(request, cancellationToken);
        public async ValueTask<PersistedAgent?> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default)
        {
            await authorize(cancellationToken);
            return await this.inner.RestoreAsync(request, cancellationToken);
        }
        public ValueTask<ChatMessage[]> ReadMessagesAsync(ReadMessagesRequest request, CancellationToken cancellationToken = default)
            => this.inner.ReadMessagesAsync(request, cancellationToken);
        public ValueTask AddSubAgentLinkAsync(string parentSessionId, string childSessionId, CancellationToken cancellationToken = default)
            => this.inner.AddSubAgentLinkAsync(parentSessionId, childSessionId, cancellationToken);
        public ValueTask<IReadOnlyList<AgentSessionId>> ReadSubAgentChildIdsAsync(string parentSessionId, CancellationToken cancellationToken = default)
            => this.inner.ReadSubAgentChildIdsAsync(parentSessionId, cancellationToken);
    }
}
