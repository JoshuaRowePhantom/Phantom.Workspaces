using Avalonia.Headless.XUnit;
using System;
using System.Reflection;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Gui.Shared.Utilities;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Trust;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class StartAgentSessionFromEntityShortcutHandlerTests
{
    [AvaloniaFact]
    public async Task ShouldApplyTo_WhenShortcutIsStartAgentSessionAndEntityHasPathField_ReturnsTrue()
    {
        var handler = CreateHandler();
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateEntityWithData("""{"entity-types":["entity","git"],"path":"/home/user/repo"}""");

        var result = await handler.ShouldApplyTo(viewModel, Shortcut.StartAgentSession, entity);

        Assert.True(result);
    }

    [AvaloniaFact]
    public async Task ShouldApplyTo_WhenShortcutIsStartAgentSessionAndEntityHasHomeDirectoryField_ReturnsTrue()
    {
        var handler = CreateHandler();
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateEntityWithData("""{"entity-types":["entity","user-computer-profile"],"home-directory":"C:\\Users\\tester"}""");

        var result = await handler.ShouldApplyTo(viewModel, Shortcut.StartAgentSession, entity);

        Assert.True(result);
    }

    [AvaloniaFact]
    public async Task ShouldApplyTo_WhenShortcutIsStartAgentSessionAndEntityHasNeitherField_ReturnsFalse()
    {
        var handler = CreateHandler();
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateEntityWithData("""{"entity-types":["entity","workspace"],"display-name":{"default":"My Workspace"}}""");

        var result = await handler.ShouldApplyTo(viewModel, Shortcut.StartAgentSession, entity);

        Assert.False(result);
    }

    [AvaloniaFact]
    public async Task ShouldApplyTo_WhenShortcutIsNotStartAgentSession_ReturnsFalse()
    {
        var handler = CreateHandler();
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = CreateEntityWithData("""{"entity-types":["entity","git"],"path":"/home/user/repo"}""");

        var result = await handler.ShouldApplyTo(viewModel, Shortcut.Open, entity);

        Assert.False(result);
    }

    [AvaloniaFact]
    public async Task ShouldApplyTo_WhenEntityDataIsNull_ReturnsFalse()
    {
        var handler = CreateHandler();
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        var entity = new SubscribedEntityViewModel(new EntitySnapshot
        {
            EntityId = new EntityId(Guid.NewGuid()),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, Guid.NewGuid().ToString()),
            Data = null,
            Relationships = Array.Empty<EntitySnapshot>(),
        });

        var result = await handler.ShouldApplyTo(viewModel, Shortcut.StartAgentSession, entity);

        Assert.False(result);
    }

    [AvaloniaFact]
    public async Task Handle_WhenEntityHasPathField_OpensStartAgentSessionTab()
    {
        var handler = CreateHandler();
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();
        var entity = CreateEntityWithData("""{"entity-id":"11111111-1111-1111-1111-111111111111","entity-types":["entity","git"],"names":[["git","my-repo"]],"display-name":{"default":"My Repo"},"path":"/home/user/my-repo"}""");

        var handled = await handler.Handle(viewModel, Shortcut.StartAgentSession, entity);

        Assert.True(handled);
    }

    [AvaloniaFact]
    public async Task Handle_WhenEntityHasHomeDirectoryField_OpensStartAgentSessionTab()
    {
        var handler = CreateHandler();
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();
        var entity = CreateEntityWithData("""{"entity-id":"22222222-2222-2222-2222-222222222222","entity-types":["entity","user-computer-profile"],"names":[["computer-user-profiles","users","username","tester","computers","hostname","myhost"]],"display-name":{"default":"My Profile"},"home-directory":"C:\\Users\\tester"}""");

        var handled = await handler.Handle(viewModel, Shortcut.StartAgentSession, entity);

        Assert.True(handled);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ResolveDefaultManifestEntityId_SwallowsException_LogsNotification()
    {
        var handler = CreateHandler();
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        // Replace the DataAccessLayer with a throwing one to trigger the catch block in
        // ResolveDefaultManifestEntityIdAsync (which is private, so we drive it via Handle).
        var entityBroker = GetEntityBroker(viewModel);
        entityBroker.EntityRepository.SetDataAccessLayerForTesting(new ThrowingDataAccessLayer());

        var entity = CreateEntityWithData("""{"entity-id":"44444444-4444-4444-4444-444444444444","entity-types":["entity","git-worktree","filesystem-path"],"names":[["tests","worktrees","notify-test"]],"display-name":{"default":"Notify Test"},"path":"/test/repo"}""");

        // Handle calls ResolveDefaultManifestEntityIdAsync, which calls DataAccessLayer.GetAsync →
        // throws → catch fires → notification posted. Handle still opens the tab and returns true.
        var handled = await handler.Handle(viewModel, Shortcut.StartAgentSession, entity);

        Assert.True(handled);
        Assert.NotEmpty(viewModel.NotificationService.Notifications);
    }

    private static EntityBroker GetEntityBroker(MainWindowViewModel viewModel)
    {
        var prop = typeof(MainWindowViewModel).GetProperty(
            "EntityBroker",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(prop);
        return Assert.IsType<EntityBroker>(prop!.GetValue(viewModel));
    }

    private sealed class ThrowingDataAccessLayer : Phantom.Workspaces.Data.IDataAccessLayer
    {
        public Task<Phantom.Workspaces.Data.UpdateResult> UpdateAsync(
            Phantom.Workspaces.Data.UpdateRequest request,
            System.Threading.CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ThrowingDataAccessLayer: UpdateAsync not supported in test.");

        public Task<Phantom.Workspaces.Data.GetResult> GetAsync(
            Phantom.Workspaces.Data.GetRequest request,
            System.Threading.CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ThrowingDataAccessLayer: GetAsync is intentionally unavailable in this test.");

        public Task<Phantom.Workspaces.Data.QueryResult> QueryAsync(
            Phantom.Workspaces.Data.QueryRequest request,
            System.Threading.CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ThrowingDataAccessLayer: QueryAsync not supported in test.");

        public Task<Phantom.Workspaces.Data.GetHistoryResult> GetHistoryAsync(
            Phantom.Workspaces.Data.GetHistoryRequest request,
            System.Threading.CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ThrowingDataAccessLayer: GetHistoryAsync not supported in test.");

        [Obsolete]
        public Task<Phantom.Workspaces.Data.ExportResult> ExportAsync(
            Phantom.Workspaces.Data.ExportRequest request,
            System.Threading.CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ThrowingDataAccessLayer: ExportAsync not supported in test.");

        public Task<Phantom.Workspaces.Data.GetChangedEntitiesResult> GetChangedEntitiesAsync(
            Phantom.Workspaces.Data.GetChangedEntitiesRequest request,
            System.Threading.CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ThrowingDataAccessLayer: GetChangedEntitiesAsync not supported in test.");

        public Task<Phantom.Workspaces.Data.ProcessQueueResult> ProcessQueueAsync(
            Phantom.Workspaces.Data.ProcessQueueRequest request,
            System.Threading.CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ThrowingDataAccessLayer: ProcessQueueAsync not supported in test.");

        public Task<Phantom.Workspaces.Data.ComputeEmbeddingsResult> ComputeEmbeddingsAsync(
            Phantom.Workspaces.Data.ComputeEmbeddingsRequest request,
            System.Threading.CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ThrowingDataAccessLayer: ComputeEmbeddingsAsync not supported in test.");

        public Task<Phantom.Workspaces.Data.UpdateEmbeddingsResult> UpdateEmbeddingsAsync(
            Phantom.Workspaces.Data.UpdateEmbeddingsRequest request,
            System.Threading.CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("ThrowingDataAccessLayer: UpdateEmbeddingsAsync not supported in test.");
    }

    private static StartAgentSessionFromEntityShortcutHandler CreateHandler()
    {
        var agentSessionShortcutContext = new AgentSessionShortcutContext();
        var trustedExecutorSelector = new DeferredTrustedExecutorSelector();
        var openAgentSessionShortcutHandler = new OpenAgentSessionShortcutHandler(
            agentSessionShortcutContext,
            trustedExecutorSelector,
            CreateTestRunningAgentChatTable());
        return new StartAgentSessionFromEntityShortcutHandler(
            agentSessionShortcutContext,
            openAgentSessionShortcutHandler);
    }

    private static RunningAgentChatTable CreateTestRunningAgentChatTable()
    {
        var store = new InMemoryAgentPersistenceStore();
        var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();
        var factory = new AgentChatFactory(store, new AgentServices(), foregroundScheduler);
        return new RunningAgentChatTable(factory);
    }

    private static SubscribedEntityViewModel CreateEntityWithData(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new SubscribedEntityViewModel(
            new EntitySnapshot
            {
                EntityId = new EntityId(Guid.NewGuid()),
                ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, Guid.NewGuid().ToString()),
                Data = document.RootElement.Clone(),
                Relationships = Array.Empty<EntitySnapshot>(),
            });
    }
}

