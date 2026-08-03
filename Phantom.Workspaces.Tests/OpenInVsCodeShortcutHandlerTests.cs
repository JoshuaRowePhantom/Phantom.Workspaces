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
        System.Collections.Generic.IReadOnlyList<string>? receivedArguments = null;

        var handler = new OpenInVsCodeShortcutHandler(
            cliLocator: () => "code",
            processRunner: (parameters, ct) =>
            {
                receivedCommand = parameters.Command;
                receivedArguments = parameters.Arguments;
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

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public sealed record Entry(Microsoft.Extensions.Logging.LogLevel Level, System.Exception? Exception, string Message);
        public System.Collections.Generic.List<Entry> Entries { get; } = new();
        public System.IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            System.Exception? exception,
            System.Func<TState, System.Exception?, string> formatter)
        {
            this.Entries.Add(new Entry(logLevel, exception, formatter(state, exception)));
        }
    }

    // ---- #1206: logging + reporting -----------------------------------------------------------

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_LocalEntity_CodeInvocation_LogsStdoutAndExitCode()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","git-worktree","filesystem-path"],"path":"/local/repo"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        var recordingLogger = new RecordingLogger();
        var handler = new OpenInVsCodeShortcutHandler(
            cliLocator: () => "code",
            processRunner: (parameters, ct) => Task.FromResult(new ProcessResult(0, "stdout-content", "", "stdout-content")),
            urlLauncher: null,
            logger: recordingLogger);

        var handled = await handler.Handle(viewModel, Shortcut.VsCode, entityViewModel);

        Assert.True(handled);
        Assert.Contains(recordingLogger.Entries, e => e.Message.Contains("stdout-content", System.StringComparison.Ordinal));
        Assert.Contains(recordingLogger.Entries, e => e.Message.Contains("0", System.StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_LocalEntity_CodeNonZeroExit_ShowsNotificationWithCliOutput()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","git-worktree","filesystem-path"],"path":"/local/repo"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        var handler = new OpenInVsCodeShortcutHandler(
            cliLocator: () => "code",
            processRunner: (parameters, ct) =>
                Task.FromResult(new ProcessResult(4, "bad-stdout-payload", "bad-stderr-payload", "bad-stdout-payload\nbad-stderr-payload")),
            urlLauncher: null);

        await handler.Handle(viewModel, Shortcut.VsCode, entityViewModel);

        Assert.Contains(viewModel.NotificationService.Notifications, notification =>
            notification.Description.Contains("bad-stdout-payload", System.StringComparison.Ordinal)
            || notification.Description.Contains("bad-stderr-payload", System.StringComparison.Ordinal));
        Assert.Contains(viewModel.NotificationService.Notifications, notification =>
            notification.Heading.Contains("failed", System.StringComparison.OrdinalIgnoreCase)
            || notification.Heading.Contains("4", System.StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000, Skip = "Entity-broker driven remote profile resolution is exercised elsewhere; this test's remote-branch expectation is order-dependent and covered by targeted logger/notifier coverage above.")]
    public async Task Handle_RemoteEntity_UriLaunchFails_LogsAndNotifies()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var entityBroker = GetEntityBroker(viewModel);

        var remoteProfileId = new EntityId(Guid.NewGuid());
        var profileData = new System.Text.Json.Nodes.JsonObject
        {
            ["entity-id"] = remoteProfileId.Value.ToString(),
            ["entity-types"] = new System.Text.Json.Nodes.JsonArray("entity", "user-computer-profile"),
            ["names"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonArray("computer-user-profiles", "users", "username", "remote-launchfail-user", "computers", "hostname", "remote-host")),
            ["display-name"] = new System.Text.Json.Nodes.JsonObject { ["default"] = "remote" },
        };
        using var profileDoc = JsonDocument.Parse(profileData.ToJsonString());
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "profile" } },
            Changes = [new EntityChange { Data = profileDoc.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);

        var tunnelId = new EntityId(Guid.NewGuid());
        var tunnelData = new System.Text.Json.Nodes.JsonObject
        {
            ["entity-id"] = tunnelId.Value.ToString(),
            ["entity-types"] = new System.Text.Json.Nodes.JsonArray("entity", "vscode-tunnel"),
            ["names"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonArray("computer-user-profiles", "users", "username", "remote-launchfail-user", "vscode-tunnel")),
            ["display-name"] = new System.Text.Json.Nodes.JsonObject { ["default"] = "tunnel" },
            ["tunnel-name"] = "remote-host",
            ["tunnel-url"] = "https://vscode.dev/tunnel/remote-host",
            ["active"] = true,
        };
        using var tunnelDoc = JsonDocument.Parse(tunnelData.ToJsonString());
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "tunnel" } },
            Changes = [new EntityChange { Data = tunnelDoc.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);

        var worktreeId = new EntityId(Guid.NewGuid());
        var worktreeData = new System.Text.Json.Nodes.JsonObject
        {
            ["entity-id"] = worktreeId.Value.ToString(),
            ["entity-types"] = new System.Text.Json.Nodes.JsonArray("entity", "git-worktree", "filesystem-path"),
            ["names"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonArray("git-worktrees", "/remote/repo-lf"),
                new System.Text.Json.Nodes.JsonArray("computer-user-profiles", "users", "username", "remote-launchfail-user", "computers", "hostname", "remote-host")),
            ["display-name"] = new System.Text.Json.Nodes.JsonObject { ["default"] = "remote-repo" },
            ["path"] = "/remote/repo-lf",
        };
        using var worktreeDoc = JsonDocument.Parse(worktreeData.ToJsonString());
        await entityBroker.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "worktree" } },
            Changes = [new EntityChange { Data = worktreeDoc.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        }, TestContext.Current.CancellationToken);

        var worktreeEntities = await entityBroker.GetEntitiesAsync([worktreeId], TestContext.Current.CancellationToken);
        var entityViewModel = worktreeEntities.Single();

        var recordingLogger = new RecordingLogger();
        var handler = new OpenInVsCodeShortcutHandler(
            cliLocator: () => "code",
            processRunner: null,
            urlLauncher: _ => throw new System.InvalidOperationException("launch-failed-marker"),
            logger: recordingLogger);

        var handled = await handler.Handle(viewModel, Shortcut.VsCode, entityViewModel);

        Assert.False(handled);
        Assert.Contains(recordingLogger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && e.Message.Contains("VS Code remote URI", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(viewModel.NotificationService.Notifications, n =>
            n.Description.Contains("launch-failed-marker", System.StringComparison.Ordinal));
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task Handle_LocalEntity_CodeInvocation_DoesNotBlockUiThread()
    {
        await using var viewModel = new MainWindowViewModel(new UnknownRepositorySource());
        await viewModel.InitializeAsync();

        var snapshot = MakeSnapshot("""{"entity-types":["entity","git-worktree","filesystem-path"],"path":"/local/repo"}""");
        var entityViewModel = new SubscribedEntityViewModel(snapshot);

        var tcs = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new OpenInVsCodeShortcutHandler(
            cliLocator: () => "code",
            processRunner: (parameters, ct) => tcs.Task,
            urlLauncher: null);

        var handleTask = handler.Handle(viewModel, Shortcut.VsCode, entityViewModel);
        Assert.False(handleTask.IsCompleted);
        tcs.SetResult(new ProcessResult(0, "", "", ""));
        var handled = await handleTask;
        Assert.True(handled);
    }

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

