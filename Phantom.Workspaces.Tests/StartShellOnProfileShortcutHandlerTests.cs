using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dock.Model.Controls;
using Dock.Model.Core;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Shell;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

[Trait("Category", "SlowLayout")]
public sealed class StartShellOnProfileShortcutHandlerTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnLocalUserComputerProfile_OpensShellTabViewModel()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var profileId = entityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        var profileEntities = await entityBroker.GetEntitiesAsync([profileId]);
        var profileEntity = Assert.Single(profileEntities);

        var fakeSession = new FakeTerminalSession();
        var handler = new StartShellOnProfileShortcutHandler(
            (_, _) => Task.FromResult<ITerminalSession>(fakeSession));

        var handled = await handler.Handle(viewModel, Shortcut.StartShell, profileEntity);

        Assert.True(handled);
        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var shellTab = documentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Select(doc => doc.TabViewModel)
            .OfType<ShellTabViewModel>()
            .FirstOrDefault();
        Assert.NotNull(shellTab);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnLocalUserComputerProfile_CreatesNoEntityOrRelationship()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var profileId = entityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        var profileEntities = await entityBroker.GetEntitiesAsync([profileId]);
        var profileEntity = Assert.Single(profileEntities);

        var snapshotsBefore = await entityBroker.EntityRepository.ExportEntitySnapshotsAsync();
        var entityCountBefore = snapshotsBefore.Count;

        var fakeSession = new FakeTerminalSession();
        var handler = new StartShellOnProfileShortcutHandler(
            (_, _) => Task.FromResult<ITerminalSession>(fakeSession));

        await handler.Handle(viewModel, Shortcut.StartShell, profileEntity);

        var snapshotsAfter = await entityBroker.EntityRepository.ExportEntitySnapshotsAsync();
        Assert.Equal(entityCountBefore, snapshotsAfter.Count);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnLocalUserComputerProfile_TabTitleContainsCommandAndHost()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var profileId = entityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        var profileEntities = await entityBroker.GetEntitiesAsync([profileId]);
        var profileEntity = Assert.Single(profileEntities);

        var fakeSession = new FakeTerminalSession();
        var handler = new StartShellOnProfileShortcutHandler(
            (_, _) => Task.FromResult<ITerminalSession>(fakeSession));

        await handler.Handle(viewModel, Shortcut.StartShell, profileEntity);

        var documentDock = GetDocumentDock(viewModel);
        Assert.NotNull(documentDock);
        var shellTab = documentDock!.VisibleDockables!
            .OfType<WorkspaceDocument>()
            .Select(doc => doc.TabViewModel)
            .OfType<ShellTabViewModel>()
            .First();

        // Title should contain some command (platform default) and the host's display name
        Assert.False(string.IsNullOrWhiteSpace(shellTab.Title));
        Assert.Contains(profileEntity.DisplayName, shellTab.Title, StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnLocalUserComputerProfile_SessionOpenerReceivesLocalClientInstance()
    {
        var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var profileId = entityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        var profileEntities = await entityBroker.GetEntitiesAsync([profileId]);
        var profileEntity = Assert.Single(profileEntities);

        string? receivedClientInstance = null;
        var handler = new StartShellOnProfileShortcutHandler(
            (target, _) =>
            {
                receivedClientInstance = target;
                return Task.FromResult<ITerminalSession>(new FakeTerminalSession());
            });

        await handler.Handle(viewModel, Shortcut.StartShell, profileEntity);

        // The local machine's profile should resolve to the local client instance "."
        Assert.Equal(".", receivedClientInstance);
    }

    private static EntityBroker GetEntityBroker(MainWindowViewModel viewModel)
    {
        var entityBrokerProperty = typeof(MainWindowViewModel).GetProperty(
            "EntityBroker",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(entityBrokerProperty);
        return Assert.IsType<EntityBroker>(entityBrokerProperty!.GetValue(viewModel));
    }

    private static IDocumentDock? GetDocumentDock(MainWindowViewModel viewModel)
    {
        var contentLayout = viewModel.SelectedWorkspacePane?.ContentLayout;
        return contentLayout is null ? null : FindDocumentDockIn(contentLayout);
    }

    private static IDocumentDock? FindDocumentDockIn(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock)
        {
            return documentDock;
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                var result = FindDocumentDockIn(child);
                if (result is not null)
                {
                    return result;
                }
            }
        }

        return null;
    }

    private sealed class FakeTerminalSession : ITerminalSession
    {
        private readonly MemoryStream stream = new();

        public Stream Stream => this.stream;

        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask SignalAsync(string signal, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public Task<int> WaitForExitAsync() => Task.FromResult(0);

        public ValueTask DisposeAsync()
        {
            this.stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
