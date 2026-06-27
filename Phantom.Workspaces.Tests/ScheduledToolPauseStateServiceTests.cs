using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.ScheduledTools;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ScheduledToolPauseStateServiceTests
{
    private sealed class FixedTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 6, 17, 9, 30, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => this.Now;
    }

    private sealed class BlockingTool : IWorkspaceTool
    {
        private readonly TaskCompletionSource started;

        public BlockingTool(TaskCompletionSource started) => this.started = started;

        public string ToolType => "stub";

        public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
        {
            this.started.SetResult();
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
            return new WorkspaceToolExecutionResult();
        }
    }

    private static readonly string[] HostName = ["computer", "this-machine"];

    private static async Task AddEntityAsync(IDataAccessLayer dataAccessLayer, Guid id, string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId(id),
                    ConcurrencyTag = null,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });
        Assert.DoesNotContain(result.EntityResults, r => r.UpdateState == UpdateState.Failed);
    }

    private static async Task AddHostProfileAsync(IDataAccessLayer dataAccessLayer, Guid hostId, bool? paused = null)
    {
        var pausedJson = paused is null ? string.Empty : $", \"scheduled-tools-paused\": {(paused.Value ? "true" : "false")}";
        await AddEntityAsync(dataAccessLayer, hostId,
            $$"""{ "entity-id": "{{hostId}}", "entity-types": ["entity", "user-computer-profile"], "names": [["computer-user-profiles","users","username","test-user","computers","hostname","this-machine"]], "user-reference": ["users","username","test-user"], "computer-reference": ["computers","hostname","this-machine"]{{pausedJson}} }""");
    }

    private static ScheduledToolPauseStateService CreateService(IDataAccessLayer dataAccessLayer)
    {
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]), timeProvider: new FixedTimeProvider());
        return new ScheduledToolPauseStateService(dataAccessLayer, host);
    }

    [Fact]
    public async Task RefreshAsync_DefaultsToFalse_WhenFlagAbsent()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        await AddHostProfileAsync(dataAccessLayer, hostId);

        var service = CreateService(dataAccessLayer);

        Assert.False(await service.RefreshAsync(new EntityId(hostId)));
        Assert.False(service.IsPaused);
    }

    [Fact]
    public async Task SetPausedAsync_PersistsState_ThatSurvivesAcrossServiceInstances()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        await AddHostProfileAsync(dataAccessLayer, hostId);

        await CreateService(dataAccessLayer).SetPausedAsync(new EntityId(hostId), paused: true);

        // A fresh service instance (simulating an app restart) reads the persisted flag.
        var restartedService = CreateService(dataAccessLayer);
        Assert.True(await restartedService.RefreshAsync(new EntityId(hostId)));
        Assert.True(restartedService.IsPaused);
    }

    [Fact]
    public async Task SetPausedAsync_RaisesPauseStateChanged_OnlyWhenStateChanges()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        await AddHostProfileAsync(dataAccessLayer, hostId);

        var service = CreateService(dataAccessLayer);
        var changeCount = 0;
        service.PauseStateChanged += (_, _) => changeCount++;

        await service.SetPausedAsync(new EntityId(hostId), paused: true);
        await service.SetPausedAsync(new EntityId(hostId), paused: true);
        await service.SetPausedAsync(new EntityId(hostId), paused: false);

        Assert.Equal(2, changeCount);
    }

    [Fact]
    public async Task SetPausedAsync_True_CancelsRunningTool()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await AddEntityAsync(dataAccessLayer, userId,
            $$"""{ "entity-id": "{{userId}}", "entity-types": ["entity", "user"], "names": [["users","username","test-user"]] }""");
        await AddEntityAsync(dataAccessLayer, computerId,
            $$"""{ "entity-id": "{{computerId}}", "entity-types": ["entity", "computer"], "names": [["computers","hostname","this-machine"]] }""");
        await AddHostProfileAsync(dataAccessLayer, hostId);
        await AddEntityAsync(dataAccessLayer, toolId,
            $$"""{ "entity-id": "{{toolId}}", "entity-types": ["entity", "tool"], "names": [["tools","stub"]], "tool-type": "stub" }""");
        await AddEntityAsync(dataAccessLayer, scheduleId,
            $$"""{ "entity-id": "{{scheduleId}}", "entity-types": ["entity", "schedule"], "names": [["schedule","test"]], "repeat": { "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] } }""");
        await AddEntityAsync(dataAccessLayer, relationshipId,
            $$"""{ "entity-id": "{{relationshipId}}", "entity-types": ["entity", "tool-relationship"], "names": [["tool-relationships","test"]], "participants": { "tool": "{{toolId}}", "schedule": ["{{scheduleId}}"], "target": ["{{hostId}}"] } }""");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([new BlockingTool(started)]), timeProvider: new FixedTimeProvider());
        var service = new ScheduledToolPauseStateService(dataAccessLayer, host);

        var runTask = host.RunDueToolsAsync(new EntityId(hostId), HostName);
        await started.Task;

        await service.SetPausedAsync(new EntityId(hostId), paused: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runTask);
        Assert.True(service.IsPaused);
    }
}
