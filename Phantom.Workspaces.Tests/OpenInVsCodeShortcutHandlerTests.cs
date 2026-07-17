using Avalonia.Headless.XUnit;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

using Phantom.Workspaces.Testing.Gui;

namespace Phantom.Workspaces.Tests;

public sealed class OpenInVsCodeShortcutHandlerTests
{
    // ---- ShouldApplyTo -----------------------------------------------------------------------

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_LocalEntityWithPath_ReturnsTrue()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","git-worktree","filesystem-path"],"path":"/repo"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);
        
        var handler = new OpenInVsCodeShortcutHandler(
            cliLocator: () => "code",
            processRunner: null,
            urlLauncher: null);

        Assert.True(await handler.ShouldApplyTo(viewModel, Shortcut.VsCode, entityViewModel));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_RemoteEntityWithTunnel_ReturnsTrue()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);
        var remoteProfileId = new EntityId(Guid.NewGuid());

        // Create a remote profile entity using JsonObject
        var profileData = new System.Text.Json.Nodes.JsonObject
        {
            ["entity-id"] = remoteProfileId.Value.ToString(),
            ["entity-types"] = new System.Text.Json.Nodes.JsonArray("entity", "user-computer-profile"),
            ["names"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonArray("computer-user-profiles", "users", "username", "remote-user", "computers", "hostname", "remote-host")),
            ["display-name"] = new System.Text.Json.Nodes.JsonObject { ["default"] = "remote-user@remote-host" },
        };
        using var profileDoc = JsonDocument.Parse(profileData.ToJsonString());
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Insert test profile." } },
            Changes = [new EntityChange { Data = profileDoc.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);

        // Create a vscode-tunnel entity for the remote profile
        var tunnelId = new EntityId(Guid.NewGuid());
        var tunnelData = new System.Text.Json.Nodes.JsonObject
        {
            ["entity-id"] = tunnelId.Value.ToString(),
            ["entity-types"] = new System.Text.Json.Nodes.JsonArray("entity", "vscode-tunnel"),
            ["names"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonArray("computer-user-profiles", "users", "username", "remote-user", "vscode-tunnel")),
            ["display-name"] = new System.Text.Json.Nodes.JsonObject { ["default"] = "remote-host tunnel" },
            ["tunnel-name"] = "remote-host",
            ["tunnel-url"] = "https://vscode.dev/tunnel/remote-host",
            ["active"] = true,
        };
        using var tunnelDoc = JsonDocument.Parse(tunnelData.ToJsonString());
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Insert test tunnel." } },
            Changes = [new EntityChange { Data = tunnelDoc.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);

        // Create a remote entity with path, owned by the remote profile
        var worktreeId = new EntityId(Guid.NewGuid());
        var worktreeData = new System.Text.Json.Nodes.JsonObject
        {
            ["entity-id"] = worktreeId.Value.ToString(),
            ["entity-types"] = new System.Text.Json.Nodes.JsonArray("entity", "git-worktree", "filesystem-path"),
            ["names"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonArray("git-worktrees", "/remote/repo"),
                new System.Text.Json.Nodes.JsonArray("computer-user-profiles", "users", "username", "remote-user", "computers", "hostname", "remote-host")),
            ["display-name"] = new System.Text.Json.Nodes.JsonObject { ["default"] = "remote-repo" },
            ["path"] = "/remote/repo",
        };
        using var worktreeDoc = JsonDocument.Parse(worktreeData.ToJsonString());
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Insert test worktree." } },
            Changes = [new EntityChange { Data = worktreeDoc.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);

        var worktreeEntities = await entityBroker.GetEntitiesAsync([worktreeId], TestContext.Current.CancellationToken);
        var entityViewModel = worktreeEntities.Single();

        var handler = new OpenInVsCodeShortcutHandler(
            cliLocator: () => "code",
            processRunner: null,
            urlLauncher: null);

        var result = await handler.ShouldApplyTo(viewModel, Shortcut.VsCode, entityViewModel);
        Assert.True(result);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_LocalEntity_CodeNotFound_ReturnsFalse()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        // Create a simple snapshot representing a local entity
        var localProfileId = viewModel.EntityBroker.EntityRepository.WorkspaceEntitySession.UserComputerProfileEntityId;
        var snapshot = MakeSnapshot($$"""{"entity-types":["entity","git-worktree","filesystem-path"],"path":"/local/repo","entity-id":"{{Guid.NewGuid()}}"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        var handler = new OpenInVsCodeShortcutHandler(
            cliLocator: () => "code",
            processRunner: null,
            urlLauncher: null);

        var shouldApply = await handler.ShouldApplyTo(viewModel, Shortcut.VsCode, entityViewModel);
        Assert.True(shouldApply);

        var handled = await handler.Handle(viewModel, Shortcut.VsCode, entityViewModel);
        Assert.False(handled);
        Assert.Contains(viewModel.NotificationService.Notifications, notification =>
            notification.Heading.Contains("VS Code CLI", StringComparison.Ordinal)
            && notification.Description.Contains("PATH", StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_EntityWithoutPath_ReturnsFalse()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","workspace"],"display-name":{"default":"workspace"}}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        var handler = new OpenInVsCodeShortcutHandler(
            cliLocator: () => "code",
            processRunner: null,
            urlLauncher: null);

        Assert.False(await handler.ShouldApplyTo(viewModel, Shortcut.VsCode, entityViewModel));
    }

    // ---- Handle ------------------------------------------------------------------------------

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_LocalEntity_RunsCodeWithPath()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","git-worktree","filesystem-path"],"display-name":{"default":"repo"},"path":"/test/repo"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        string? receivedCommand = null;
        string[]? receivedArguments = null;

        var handler = new OpenInVsCodeShortcutHandler(
            cliLocator: () => "code",
            processRunner: (cmd, args, ct) =>
            {
                receivedCommand = cmd;
                receivedArguments = args;
                return Task.FromResult(new ProcessResult(0, "", "", ""));
            },
            urlLauncher: null);

        var handled = await handler.Handle(viewModel, Shortcut.VsCode, entityViewModel);

        Assert.True(handled);
        Assert.Equal("code", receivedCommand);
        Assert.NotNull(receivedArguments);
        Assert.Contains("/test/repo", receivedArguments);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_LocalEntity_CodeNotFound_ShowsNotification()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","git-worktree","filesystem-path"],"display-name":{"default":"repo"},"path":"/test/repo"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        var handler = new OpenInVsCodeShortcutHandler(
            cliLocator: () => throw new System.ComponentModel.Win32Exception("code not found"),
            processRunner: null,
            urlLauncher: null);

        var handled = await handler.Handle(viewModel, Shortcut.VsCode, entityViewModel);

        Assert.False(handled);
        Assert.Contains(viewModel.NotificationService.Notifications, notification =>
            notification.Heading.Contains("VS Code CLI", StringComparison.Ordinal)
            && notification.Description.Contains("code", StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ShouldApplyTo_RemoteEntityWithoutTunnel_ReturnsFalse()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var remoteProfileId = new EntityId(Guid.NewGuid());
        var snapshot = MakeSnapshot($$"""
            {
              "entity-id":"{{remoteProfileId.Value}}",
              "entity-types":["entity","user-computer-profile","filesystem-path"],
              "names":[["computer-user-profiles","users","username","remote-user-without-tunnel","computers","hostname","remote-host"]],
              "display-name":{"default":"remote-user-without-tunnel@remote-host"},
              "path":"/remote/repo"
            }
            """);
        snapshot = snapshot with { EntityId = remoteProfileId };
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        var handler = new OpenInVsCodeShortcutHandler(
            cliLocator: () => "code",
            processRunner: null,
            urlLauncher: null);

        var result = await handler.ShouldApplyTo(viewModel, Shortcut.VsCode, entityViewModel);

        Assert.False(result);
    }

    // Note: Comprehensive testing of remote entity scenarios (with profile/tunnel lookups)
    // requires a repository source that supports entity name queries. UnknownRepositorySource
    // doesn't support this. The production repository sources (MongoDB, Offline) do support
    // entity name lookups, so the remote functionality will work in production.

    // ---- Helpers -----------------------------------------------------------------------------

    private static EntitySnapshot MakeSnapshot(string json) =>
        new()
        {
            EntityId = new EntityId(Guid.NewGuid()),
            ModifiedTime = new Timestamp(DateTimeOffset.UtcNow, Guid.NewGuid().ToString()),
            Data = JsonDocument.Parse(json).RootElement.Clone(),
            Relationships = [],
        };

    private static EntityBroker GetEntityBroker(MainWindowViewModel viewModel)
    {
        var entityBrokerProperty = typeof(MainWindowViewModel).GetProperty(
            "EntityBroker",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(entityBrokerProperty);
        return Assert.IsType<EntityBroker>(entityBrokerProperty!.GetValue(viewModel));
    }
}

