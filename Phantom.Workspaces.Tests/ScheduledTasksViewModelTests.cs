using System.Linq;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ScheduledTools;
using Phantom.Workspaces.Tools;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class ScheduledTasksViewModelTests
{
    [AvaloniaFact]
    public async Task RefreshAsync_LoadsScheduledTasks_WithResolvedParticipantNames()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        var toolId = new EntityId("f7a8b9c0-d1e2-4f5a-6b7c-8d9e0f1a2b3c");
        var scheduleId = new EntityId("0b784370-6ba2-4e43-812f-b1ef2bef239c");
        var targetId = new EntityId("a1b2c3d4-1111-2222-3333-444455556666");
        var relationshipId = new EntityId("a638fe05-9dd5-49f8-a0b8-c767de434b6f");

        await SeedAsync(broker, $$"""
            { "entity-id": "{{toolId}}", "entity-types": ["entity", "folder"], "names": [["tools","git-workspace-scan"]], "display-name": { "default": "Git Workspace Scan" } }
            """);
        await SeedAsync(broker, $$"""
            { "entity-id": "{{scheduleId}}", "entity-types": ["entity", "folder"], "names": [["schedule","every-five-minutes"]], "display-name": { "default": "Every five minutes" } }
            """);
        await SeedAsync(broker, $$"""
            { "entity-id": "{{targetId}}", "entity-types": ["entity", "folder"], "names": [["profiles","jrowe-daemon"]], "display-name": { "default": "jrowe @ DAEMON" } }
            """);
        await SeedAsync(broker, $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["entity", "tool-relationship", "relationship"],
              "names": [["relationship","{{relationshipId}}"]],
              "note": "Scheduling Git Workspace Scan every five minutes.",
              "participants": {
                "tool": "{{toolId}}",
                "schedule": ["{{scheduleId}}"],
                "target": ["{{targetId}}"]
              }
            }
            """);

        var viewModel = new ScheduledTasksViewModel(broker);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        var task = Assert.Single(viewModel.ScheduledTasks);
        Assert.Equal("Git Workspace Scan", task.ToolDisplayName);
        Assert.Equal("Every five minutes", task.ScheduleDisplayName);
        Assert.Equal("jrowe @ DAEMON", task.TargetDisplayName);
        Assert.True(task.HasNote);
        Assert.True(viewModel.HasScheduledTasks);
    }

    [AvaloniaFact]
    public async Task TogglePause_PersistsHostPauseState_AndUpdatesButtonText()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        var hostId = new EntityId("b2c3d4e5-1111-2222-3333-444455556666");
        await SeedAsync(broker, $$"""
            {
              "entity-id": "{{hostId}}",
              "entity-types": ["entity", "user-computer-profile"],
              "names": [["computer-user-profiles","users","username","test-user","computers","hostname","this-machine"]],
              "user-reference": ["users","username","test-user"],
              "computer-reference": ["computers","hostname","this-machine"]
            }
            """);

        var dataAccessLayer = broker.EntityRepository.DataAccessLayer;
        var host = new Phantom.Workspaces.ScheduledTools.ScheduledToolHost(
            dataAccessLayer,
            new Phantom.Workspaces.ScheduledTools.ScheduledToolRegistry([]));
        var pauseStateService = new Phantom.Workspaces.ScheduledTools.ScheduledToolPauseStateService(
            dataAccessLayer,
            host);

        var viewModel = new ScheduledTasksViewModel(broker, pauseStateService, hostId);

        Assert.True(viewModel.HasPauseControl);
        Assert.False(viewModel.IsPaused);
        Assert.Equal("Stop all / Pause", viewModel.PauseButtonText);

        await viewModel.TogglePauseAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsPaused);
        Assert.Equal("Resume scheduled tools", viewModel.PauseButtonText);

        // A fresh service instance reads back the persisted flag (survives across instances).
        var restarted = new Phantom.Workspaces.ScheduledTools.ScheduledToolPauseStateService(dataAccessLayer, host);
        Assert.True(await restarted.RefreshAsync(hostId, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task HasPauseControl_IsFalse_WhenNoPauseServiceSupplied()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        var viewModel = new ScheduledTasksViewModel(broker);

        Assert.False(viewModel.HasPauseControl);
        Assert.False(viewModel.IsPaused);
    }

    [AvaloniaFact]
    public async Task ScheduledToolsRunning_IsNull_WhenNoHostProvided()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        using var viewModel = new ScheduledTasksViewModel(broker);

        Assert.Null(viewModel.ScheduledToolsRunning);
    }

    [AvaloniaFact]
    public async Task ScheduledToolsRunning_IsNotNull_WhenHostProvided()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        var host = new Phantom.Workspaces.ScheduledTools.ScheduledToolHost(
            broker.EntityRepository.DataAccessLayer,
            new Phantom.Workspaces.ScheduledTools.ScheduledToolRegistry([]));

        using var viewModel = new ScheduledTasksViewModel(broker, scheduledToolHost: host);

        Assert.NotNull(viewModel.ScheduledToolsRunning);
    }

    [AvaloniaFact]
    public async Task SelectedTask_WhenSet_SelectedToolRowReturnsMatchingToolRow()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        var toolId = new EntityId("c7a8b9c0-d1e2-4f5a-6b7c-8d9e0f1a2b3c");
        var scheduleId = new EntityId("1b784370-6ba2-4e43-812f-b1ef2bef239c");
        var targetId = new EntityId("b1b2c3d4-1111-2222-3333-444455556666");
        var relationshipId = new EntityId("b638fe05-9dd5-49f8-a0b8-c767de434b6f");

        await SeedAsync(broker, $$"""
            { "entity-id": "{{toolId}}", "entity-types": ["entity", "tool"], "names": [["tools","stub"]], "display-name": { "default": "Stub Tool" }, "tool-type": "stub" }
            """);
        await SeedAsync(broker, $$"""
            { "entity-id": "{{scheduleId}}", "entity-types": ["entity", "schedule"], "names": [["schedule","every-minute"]], "display-name": { "default": "Every minute" }, "repeat": { "frequency": "00:01:00Z", "days-of-week": [], "start-at": [] } }
            """);
        await SeedAsync(broker, $$"""
            { "entity-id": "{{targetId}}", "entity-types": ["entity", "folder"], "names": [["profiles","test-host"]], "display-name": { "default": "Test Host" } }
            """);
        await SeedAsync(broker, $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["entity", "tool-relationship", "relationship"],
              "names": [["tool-relationships","{{relationshipId}}"]],
              "participants": { "tool": "{{toolId}}", "schedule": ["{{scheduleId}}"], "target": ["{{targetId}}"] }
            }
            """);

        var writer = new ToolExecutionResultWriter(broker.EntityRepository.DataAccessLayer);
        var handle = await writer.StartAsync(["host", "machine"], "stub", TestContext.Current.CancellationToken);
        await writer.CompleteAsync(handle, success: true, cancellationToken: TestContext.Current.CancellationToken);

        var host = new ScheduledToolHost(
            broker.EntityRepository.DataAccessLayer,
            new ScheduledToolRegistry([]));

        using var viewModel = new ScheduledTasksViewModel(broker, scheduledToolHost: host);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Null(viewModel.SelectedTask);
        Assert.Null(viewModel.SelectedToolRow);

        var task = Assert.Single(viewModel.ScheduledTasks);
        Assert.Equal("stub", task.ToolType);

        viewModel.SelectedTask = task;

        Assert.NotNull(viewModel.SelectedToolRow);
        Assert.Equal("stub", viewModel.SelectedToolRow.ToolType);
    }

    [AvaloniaFact]
    public async Task SelectedTask_WhenClearedToNull_SelectedToolRowIsNull()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        var host = new ScheduledToolHost(
            broker.EntityRepository.DataAccessLayer,
            new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledTasksViewModel(broker, scheduledToolHost: host);

        Assert.Null(viewModel.SelectedTask);
        Assert.Null(viewModel.SelectedToolRow);
    }

    [AvaloniaFact]
    public async Task ScheduledTaskItemViewModel_HasFailure_SyncedFromRunningViewModelOnRefresh()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        var toolId = new EntityId("d7a8b9c0-d1e2-4f5a-6b7c-8d9e0f1a2b3c");
        var scheduleId = new EntityId("2b784370-6ba2-4e43-812f-b1ef2bef239c");
        var targetId = new EntityId("c1b2c3d4-1111-2222-3333-444455556666");
        var relationshipId = new EntityId("c638fe05-9dd5-49f8-a0b8-c767de434b6f");

        await SeedAsync(broker, $$"""
            { "entity-id": "{{toolId}}", "entity-types": ["entity", "tool"], "names": [["tools","stub"]], "display-name": { "default": "Stub Tool" }, "tool-type": "stub" }
            """);
        await SeedAsync(broker, $$"""
            { "entity-id": "{{scheduleId}}", "entity-types": ["entity", "schedule"], "names": [["schedule","every-minute"]], "display-name": { "default": "Every minute" }, "repeat": { "frequency": "00:01:00Z", "days-of-week": [], "start-at": [] } }
            """);
        await SeedAsync(broker, $$"""
            { "entity-id": "{{targetId}}", "entity-types": ["entity", "folder"], "names": [["profiles","test-host"]], "display-name": { "default": "Test Host" } }
            """);
        await SeedAsync(broker, $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["entity", "tool-relationship", "relationship"],
              "names": [["tool-relationships","{{relationshipId}}"]],
              "participants": { "tool": "{{toolId}}", "schedule": ["{{scheduleId}}"], "target": ["{{targetId}}"] }
            }
            """);

        var writer = new ToolExecutionResultWriter(broker.EntityRepository.DataAccessLayer);
        var handle = await writer.StartAsync(["host", "machine"], "stub", TestContext.Current.CancellationToken);
        await writer.CompleteAsync(handle, success: false, cancellationToken: TestContext.Current.CancellationToken);

        var host = new ScheduledToolHost(
            broker.EntityRepository.DataAccessLayer,
            new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledTasksViewModel(broker, scheduledToolHost: host);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        var task = Assert.Single(viewModel.ScheduledTasks);
        Assert.True(task.HasFailure);
        Assert.False(task.LastRunSucceeded);
        Assert.False(task.IsRunning);
    }

    [AvaloniaFact]
    public async Task SelectedTask_WhenTaskHasNoToolRow_SelectedToolRowIsNull()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        var toolId = new EntityId("e7a8b9c0-d1e2-4f5a-6b7c-8d9e0f1a2b3c");
        var scheduleId = new EntityId("3b784370-6ba2-4e43-812f-b1ef2bef239c");
        var targetId = new EntityId("d1b2c3d4-1111-2222-3333-444455556666");
        var relationshipId = new EntityId("d638fe05-9dd5-49f8-a0b8-c767de434b6f");

        await SeedAsync(broker, $$"""
            { "entity-id": "{{toolId}}", "entity-types": ["entity", "tool"], "names": [["tools","run-vscode-tunnel"]], "display-name": { "default": "Run VS Code Tunnel" }, "tool-type": "run-vscode-tunnel" }
            """);
        await SeedAsync(broker, $$"""
            { "entity-id": "{{scheduleId}}", "entity-types": ["entity", "schedule"], "names": [["schedule","every-hour"]], "display-name": { "default": "Every hour" }, "repeat": { "frequency": "01:00:00Z", "days-of-week": [], "start-at": [] } }
            """);
        await SeedAsync(broker, $$"""
            { "entity-id": "{{targetId}}", "entity-types": ["entity", "folder"], "names": [["profiles","test-host"]], "display-name": { "default": "Test Host" } }
            """);
        await SeedAsync(broker, $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["entity", "tool-relationship", "relationship"],
              "names": [["tool-relationships","{{relationshipId}}"]],
              "participants": { "tool": "{{toolId}}", "schedule": ["{{scheduleId}}"], "target": ["{{targetId}}"] }
            }
            """);

        // No tool-execution-result entities written — the registry is empty, so no ToolRowViewModel exists.
        var host = new ScheduledToolHost(
            broker.EntityRepository.DataAccessLayer,
            new ScheduledToolRegistry([]));

        using var viewModel = new ScheduledTasksViewModel(broker, scheduledToolHost: host);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        var task = Assert.Single(viewModel.ScheduledTasks);
        Assert.Equal("run-vscode-tunnel", task.ToolType);

        viewModel.SelectedTask = task;

        Assert.Null(viewModel.SelectedToolRow);
    }

    [AvaloniaFact]
    public async Task SelectedTask_WhenTaskHasNoToolRow_HasNoRunsForSelectedTask_IsTrue()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        var toolId = new EntityId("f7a8b9c0-d1e2-4f5a-6b7c-8d9e0f1a2b4d");
        var scheduleId = new EntityId("4b784370-6ba2-4e43-812f-b1ef2bef239c");
        var targetId = new EntityId("e1b2c3d4-1111-2222-3333-444455556666");
        var relationshipId = new EntityId("e638fe05-9dd5-49f8-a0b8-c767de434b6f");

        await SeedAsync(broker, $$"""
            { "entity-id": "{{toolId}}", "entity-types": ["entity", "tool"], "names": [["tools","run-vscode-tunnel2"]], "display-name": { "default": "Run VS Code Tunnel" }, "tool-type": "run-vscode-tunnel" }
            """);
        await SeedAsync(broker, $$"""
            { "entity-id": "{{scheduleId}}", "entity-types": ["entity", "schedule"], "names": [["schedule","every-two-hours"]], "display-name": { "default": "Every two hours" }, "repeat": { "frequency": "02:00:00Z", "days-of-week": [], "start-at": [] } }
            """);
        await SeedAsync(broker, $$"""
            { "entity-id": "{{targetId}}", "entity-types": ["entity", "folder"], "names": [["profiles","test-host-2"]], "display-name": { "default": "Test Host 2" } }
            """);
        await SeedAsync(broker, $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["entity", "tool-relationship", "relationship"],
              "names": [["tool-relationships","{{relationshipId}}"]],
              "participants": { "tool": "{{toolId}}", "schedule": ["{{scheduleId}}"], "target": ["{{targetId}}"] }
            }
            """);

        var host = new ScheduledToolHost(
            broker.EntityRepository.DataAccessLayer,
            new ScheduledToolRegistry([]));

        using var viewModel = new ScheduledTasksViewModel(broker, scheduledToolHost: host);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        var task = Assert.Single(viewModel.ScheduledTasks);

        Assert.False(viewModel.HasNoRunsForSelectedTask);

        viewModel.SelectedTask = task;

        Assert.Null(viewModel.SelectedToolRow);
        Assert.True(viewModel.HasNoRunsForSelectedTask);
    }

    [AvaloniaFact]
    public async Task SelectedTask_WhenClearedAfterSelection_HasNoRunsForSelectedTask_IsFalse()
    {
        var broker = await EntityBroker.CreateInitializedAsync(
            new UnknownRepositorySource(),
            TestContext.Current.CancellationToken);

        var toolId = new EntityId("17a8b9c0-d1e2-4f5a-6b7c-8d9e0f1a2b3c");
        var scheduleId = new EntityId("5b784370-6ba2-4e43-812f-b1ef2bef239c");
        var targetId = new EntityId("11b2c3d4-1111-2222-3333-444455556666");
        var relationshipId = new EntityId("1638fe05-9dd5-49f8-a0b8-c767de434b6f");

        await SeedAsync(broker, $$"""
            { "entity-id": "{{toolId}}", "entity-types": ["entity", "tool"], "names": [["tools","unregistered-tool"]], "display-name": { "default": "Unregistered Tool" }, "tool-type": "unregistered-tool" }
            """);
        await SeedAsync(broker, $$"""
            { "entity-id": "{{scheduleId}}", "entity-types": ["entity", "schedule"], "names": [["schedule","every-three-hours"]], "display-name": { "default": "Every three hours" }, "repeat": { "frequency": "03:00:00Z", "days-of-week": [], "start-at": [] } }
            """);
        await SeedAsync(broker, $$"""
            { "entity-id": "{{targetId}}", "entity-types": ["entity", "folder"], "names": [["profiles","test-host-3"]], "display-name": { "default": "Test Host 3" } }
            """);
        await SeedAsync(broker, $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["entity", "tool-relationship", "relationship"],
              "names": [["tool-relationships","{{relationshipId}}"]],
              "participants": { "tool": "{{toolId}}", "schedule": ["{{scheduleId}}"], "target": ["{{targetId}}"] }
            }
            """);

        var host = new ScheduledToolHost(
            broker.EntityRepository.DataAccessLayer,
            new ScheduledToolRegistry([]));

        using var viewModel = new ScheduledTasksViewModel(broker, scheduledToolHost: host);
        await viewModel.RefreshAsync(TestContext.Current.CancellationToken);

        var task = Assert.Single(viewModel.ScheduledTasks);
        viewModel.SelectedTask = task;

        Assert.True(viewModel.HasNoRunsForSelectedTask);

        viewModel.SelectedTask = null;

        Assert.False(viewModel.HasNoRunsForSelectedTask);
    }

    private static async Task SeedAsync(
        EntityBroker broker,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        await broker.EntityRepository.DataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "Seed scheduled tasks test." } },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = new EntityId(document.RootElement.GetProperty("entity-id").GetString()!),
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            },
            TestContext.Current.CancellationToken);
    }
}
