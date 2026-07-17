using Avalonia.Headless.XUnit;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dock.Model.Controls;
using Dock.Model.Core;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Shell;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class StartShellFromEntityShortcutHandlerTests
{
    // ---- ShouldApplyTo -----------------------------------------------------------------------

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_EntityWithPathField_ReturnsTrue()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","git-worktree","filesystem-path"],"path":"/repo"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);
        var handler = new StartShellFromEntityShortcutHandler((_, _, _) => throw new InvalidOperationException());

        Assert.True(await handler.ShouldApplyTo(viewModel, Shortcut.StartShell, entityViewModel));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_EntityWithHomeDirectoryField_ReturnsTrue()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","user-computer-profile"],"home-directory":"C:\\Users\\tester"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);
        var handler = new StartShellFromEntityShortcutHandler((_, _, _) => throw new InvalidOperationException());

        Assert.True(await handler.ShouldApplyTo(viewModel, Shortcut.StartShell, entityViewModel));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_EntityWithoutPathOrHomeDirectory_ReturnsFalse()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","workspace"]}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);
        var handler = new StartShellFromEntityShortcutHandler((_, _, _) => throw new InvalidOperationException());

        Assert.False(await handler.ShouldApplyTo(viewModel, Shortcut.StartShell, entityViewModel));
    }

    // ---- Handle: working directory -----------------------------------------------------------

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnEntityWithPathField_OpensShellTabViewModel()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","git-worktree","filesystem-path"],"display-name":{"default":"repo"},"path":"/repo"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        var fakeSession = new FakeTerminalSession();
        var handler = new StartShellFromEntityShortcutHandler((_, _, _) => Task.FromResult<ITerminalSession>(fakeSession));

        var handled = await handler.Handle(viewModel, Shortcut.StartShell, entityViewModel);

        Assert.True(handled);
        var shellTab = FindShellTab(viewModel);
        Assert.NotNull(shellTab);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnEntityWithPathField_PassesPathAsWorkingDirectory()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","git-worktree","filesystem-path"],"display-name":{"default":"repo"},"path":"/test/repo"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        string? receivedWorkingDirectory = null;
        var handler = new StartShellFromEntityShortcutHandler(
            (_, workDir, _) =>
            {
                receivedWorkingDirectory = workDir;
                return Task.FromResult<ITerminalSession>(new FakeTerminalSession());
            });

        await handler.Handle(viewModel, Shortcut.StartShell, entityViewModel);

        Assert.Equal("/test/repo", receivedWorkingDirectory);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnEntityWithHomeDirectoryField_PassesHomeDirectoryAsWorkingDirectory()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","user-computer-profile"],"display-name":{"default":"profile"},"home-directory":"C:\\Users\\tester"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        string? receivedWorkingDirectory = null;
        var handler = new StartShellFromEntityShortcutHandler(
            (_, workDir, _) =>
            {
                receivedWorkingDirectory = workDir;
                return Task.FromResult<ITerminalSession>(new FakeTerminalSession());
            });

        await handler.Handle(viewModel, Shortcut.StartShell, entityViewModel);

        Assert.Equal(@"C:\Users\tester", receivedWorkingDirectory);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnEntityWithBothPathAndHomeDirectory_PrefersPathAsWorkingDirectory()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","git-worktree"],"display-name":{"default":"repo"},"path":"/the/path","home-directory":"C:\\Users\\tester"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        string? receivedWorkingDirectory = null;
        var handler = new StartShellFromEntityShortcutHandler(
            (_, workDir, _) =>
            {
                receivedWorkingDirectory = workDir;
                return Task.FromResult<ITerminalSession>(new FakeTerminalSession());
            });

        await handler.Handle(viewModel, Shortcut.StartShell, entityViewModel);

        Assert.Equal("/the/path", receivedWorkingDirectory);
    }

    // ---- Handle: client instance routing -----------------------------------------------------

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnLocalUserComputerProfileEntity_UsesLocalClientInstance()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var profileId = entityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        var profileEntities = await entityBroker.GetEntitiesAsync([profileId], TestContext.Current.CancellationToken);
        var profileEntity = Assert.Single(profileEntities);

        string? receivedClientInstance = null;
        var handler = new StartShellFromEntityShortcutHandler(
            (targetClientInstance, _, _) =>
            {
                receivedClientInstance = targetClientInstance;
                return Task.FromResult<ITerminalSession>(new FakeTerminalSession());
            });

        await handler.Handle(viewModel, Shortcut.StartShell, profileEntity);

        Assert.Equal(".", receivedClientInstance);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnGitWorktreeEntityBelongingToLocalProfile_UsesLocalClientInstance()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var profileId = entityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;

        // Retrieve the profile entity to read its primary name, which we will embed as a
        // secondary name on the git-worktree entity so the handler can trace ownership.
        var profileEntities = await entityBroker.GetEntitiesAsync([profileId], TestContext.Current.CancellationToken);
        var profileEntity = profileEntities.Single();
        var profilePrimaryName = ReadFirstEntityName(profileEntity.Data);
        Assert.NotNull(profilePrimaryName);

        // Insert a git-worktree entity whose secondary name is the local profile's primary name.
        var entityId = new EntityId(Guid.NewGuid());
        var entityData = new JsonObject
        {
            ["entity-id"] = entityId.Value.ToString(),
            ["entity-types"] = new JsonArray("entity", "git-worktree", "filesystem-path"),
            ["names"] = new JsonArray(
                new JsonArray("git-worktrees", "/test/worktree"),
                new JsonArray(profilePrimaryName.Select(c => (JsonNode)c).ToArray())),
            ["display-name"] = new JsonObject { ["default"] = "test-worktree" },
            ["path"] = "/test/worktree",
        };

        using var entityDocument = JsonDocument.Parse(entityData.ToJsonString());
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Insert test git-worktree entity." } },
            Changes =
            [
                new EntityChange
                {
                    Data = entityDocument.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        }, TestContext.Current.CancellationToken);

        var worktreeEntities = await entityBroker.GetEntitiesAsync([entityId], TestContext.Current.CancellationToken);
        var worktreeEntity = worktreeEntities.Single();

        string? receivedClientInstance = null;
        var handler = new StartShellFromEntityShortcutHandler(
            (targetClientInstance, _, _) =>
            {
                receivedClientInstance = targetClientInstance;
                return Task.FromResult<ITerminalSession>(new FakeTerminalSession());
            });

        await handler.Handle(viewModel, Shortcut.StartShell, worktreeEntity);

        Assert.Equal(".", receivedClientInstance);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_OnGitWorktreeEntityWithNoProfileName_FallsBackToLocalClientInstance()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        // An entity with a path but no profile secondary name — fall back to local.
        var snapshot = MakeSnapshot(
            """{"entity-types":["entity","git-worktree","filesystem-path"],"names":[["git-worktrees","/orphan"]],"display-name":{"default":"orphan"},"path":"/orphan"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        string? receivedClientInstance = null;
        var handler = new StartShellFromEntityShortcutHandler(
            (targetClientInstance, _, _) =>
            {
                receivedClientInstance = targetClientInstance;
                return Task.FromResult<ITerminalSession>(new FakeTerminalSession());
            });

        await handler.Handle(viewModel, Shortcut.StartShell, entityViewModel);

        Assert.Equal(".", receivedClientInstance);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static EntitySnapshot MakeSnapshot(string json) =>
        new()
        {
            EntityId = new EntityId(Guid.NewGuid()),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, Guid.NewGuid().ToString()),
            Data = JsonDocument.Parse(json).RootElement.Clone(),
            Relationships = [],
        };

    /// <summary>Reads the first name-component array from the entity data's "names" field.</summary>
    private static string[]? ReadFirstEntityName(JsonElement? entityData)
    {
        if (entityData is not JsonElement data
            || !data.TryGetProperty("names", out var namesElement)
            || namesElement.ValueKind != JsonValueKind.Array
            || namesElement.GetArrayLength() == 0)
        {
            return null;
        }

        var firstNameElement = namesElement[0];
        if (firstNameElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return firstNameElement.EnumerateArray()
            .Where(static e => e.ValueKind == JsonValueKind.String)
            .Select(static e => e.GetString()!)
            .ToArray();
    }

    private static EntityBroker GetEntityBroker(MainWindowViewModel viewModel)
    {
        var entityBrokerProperty = typeof(MainWindowViewModel).GetProperty(
            "EntityBroker",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(entityBrokerProperty);
        return Assert.IsType<EntityBroker>(entityBrokerProperty!.GetValue(viewModel));
    }

    private static ShellTabViewModel? FindShellTab(MainWindowViewModel viewModel)
    {
        var contentLayout = viewModel.SelectedWorkspacePane?.ContentLayout;
        if (contentLayout is null)
        {
            return null;
        }

        var documentDock = FindDocumentDock(contentLayout);
        return documentDock?.VisibleDockables?
            .OfType<WorkspaceDocument>()
            .Select(doc => doc.TabViewModel)
            .OfType<ShellTabViewModel>()
            .FirstOrDefault();
    }

    private static IDocumentDock? FindDocumentDock(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock)
        {
            return documentDock;
        }

        if (dockable is IDock dock && dock.VisibleDockables is not null)
        {
            foreach (var child in dock.VisibleDockables)
            {
                var result = FindDocumentDock(child);
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

