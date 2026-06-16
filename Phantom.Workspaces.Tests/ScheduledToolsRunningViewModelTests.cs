using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.ScheduledTools;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ScheduledToolsRunningViewModelTests
{
    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 6, 17, 9, 30, 0, TimeSpan.Zero);
    }

    private sealed class GatedTool : IScheduledTool
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ToolType => "stub";

        public async Task RunAsync(ScheduledToolContext context, CancellationToken cancellationToken)
        {
            this.Started.TrySetResult();
            await this.Release.Task;
        }
    }

    private static readonly string[] HostName = ["computer", "this-machine"];

    private static async Task AddEntityAsync(IDataAccessLayer dataAccessLayer, Guid id, string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes = [new EntityChange { EntityId = new EntityId(id), ConcurrencyTag = null, Data = document.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
        });
        Assert.DoesNotContain(result.EntityResults, r => r.UpdateState == UpdateState.Failed);
    }

    [Fact]
    public async Task RunningTools_ReflectsInFlightExecution_AndClearsOnCompletion()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await AddEntityAsync(dataAccessLayer, hostId, $$"""{ "entity-id": "{{hostId}}", "entity-types": ["computer"], "names": [["computer","this-machine"]] }""");
        await AddEntityAsync(dataAccessLayer, toolId, $$"""{ "entity-id": "{{toolId}}", "entity-types": ["tool"], "names": [["tools","stub"]], "type": "stub" }""");
        await AddEntityAsync(dataAccessLayer, scheduleId, $$"""{ "entity-id": "{{scheduleId}}", "entity-types": ["schedule"], "names": [["schedule","s"]], "repeat": { "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] } }""");
        await AddEntityAsync(dataAccessLayer, relationshipId, $$"""{ "entity-id": "{{relationshipId}}", "entity-types": ["tool-relationship"], "names": [["tool-relationships","r"]], "participants": { "tool": "{{toolId}}", "schedule": ["{{scheduleId}}"], "target": ["{{hostId}}"] } }""");

        var tool = new GatedTool();
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());
        using var viewModel = new ScheduledToolsRunningViewModel(host);

        Assert.False(viewModel.HasRunningTools);

        var runTask = host.RunDueToolsAsync(new EntityId(hostId), HostName);

        // The host adds the running execution (and raises the event) before the tool body runs, so by
        // the time the tool signals "started" the view-model already reflects the in-flight run.
        await tool.Started.Task;
        Assert.True(viewModel.HasRunningTools);
        var running = Assert.Single(viewModel.RunningTools);
        Assert.Equal("stub", running.ToolType);
        Assert.Equal("computer / this-machine", running.Host);

        tool.Release.TrySetResult();
        await runTask;

        Assert.False(viewModel.HasRunningTools);
        Assert.Empty(viewModel.RunningTools);
    }
}
