using Avalonia.Headless.XUnit;
using System;
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

