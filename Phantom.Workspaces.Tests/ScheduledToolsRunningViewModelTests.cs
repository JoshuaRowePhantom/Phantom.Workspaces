using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.ScheduledTools;
using Phantom.Workspaces.Tools;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ScheduledToolsRunningViewModelTests
{
    private sealed class FixedTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 6, 17, 9, 30, 0, TimeSpan.Zero);

        public DateTimeOffset Advance(TimeSpan by)
        {
            this.now = this.now.Add(by);
            return this.now;
        }

        public override DateTimeOffset GetUtcNow() => this.now;
    }

    private sealed class GatedTool : IWorkspaceTool
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string ToolType => "stub";

        public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
        {
            this.Started.TrySetResult();
            await this.Release.Task;
            return new WorkspaceToolExecutionResult();
        }
    }

    private static readonly string[] HostName = ["computer", "this-machine"];
    private const string HostLabel = "computer / this-machine";

    private static async Task<(InMemoryDataAccessLayer DataAccessLayer, ScheduledToolHost Host, Guid HostId)>
        CreateHostAsync(IWorkspaceTool tool)
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var hostId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await AddEntityAsync(dataAccessLayer, userId,
            $$"""{ "entity-id": "{{userId}}", "entity-types": ["entity", "user"], "names": [["users","username","test-user"]] }""");
        await AddEntityAsync(dataAccessLayer, computerId,
            $$"""{ "entity-id": "{{computerId}}", "entity-types": ["entity", "computer"], "names": [["computers","hostname","this-machine"]] }""");
        await AddEntityAsync(dataAccessLayer, hostId,
            $$"""{ "entity-id": "{{hostId}}", "entity-types": ["entity", "user-computer-profile"], "names": [["computer-user-profiles","users","username","test-user","computers","hostname","this-machine"]], "user-reference": ["users","username","test-user"], "computer-reference": ["computers","hostname","this-machine"] }""");
        await AddEntityAsync(dataAccessLayer, toolId,
            $$"""{ "entity-id": "{{toolId}}", "entity-types": ["entity", "tool"], "names": [["tools","stub"]], "tool-type": "stub" }""");
        await AddEntityAsync(dataAccessLayer, scheduleId,
            $$"""{ "entity-id": "{{scheduleId}}", "entity-types": ["entity", "schedule"], "names": [["schedule","s"]], "repeat": { "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] } }""");
        await AddEntityAsync(dataAccessLayer, relationshipId,
            $$"""{ "entity-id": "{{relationshipId}}", "entity-types": ["entity", "tool-relationship"], "names": [["tool-relationships","r"]], "participants": { "tool": "{{toolId}}", "schedule": ["{{scheduleId}}"], "target": ["{{hostId}}"] } }""");

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]));
        return (dataAccessLayer, host, hostId);
    }

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

    private static async Task WriteRunAsync(
        IDataAccessLayer dataAccessLayer,
        FixedTimeProvider timeProvider,
        string toolName,
        bool success,
        string? message = null)
    {
        var writer = new ToolExecutionResultWriter(dataAccessLayer, timeProvider);
        var handle = await writer.StartAsync(HostName, toolName, TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await writer.CompleteAsync(handle, success, message, TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Tools_ReflectsInFlightExecution_AndClearsIsRunningOnCompletion()
    {
        var tool = new GatedTool();
        var (dataAccessLayer, host, hostId) = await CreateHostAsync(tool);
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);

        Assert.False(viewModel.HasRunningTools);

        var runTask = host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);

        await tool.Started.Task;
        Assert.True(viewModel.HasRunningTools);

        var row = Assert.Single(viewModel.Tools);
        Assert.Equal("stub", row.ToolType);
        Assert.Equal(HostLabel, row.Host);
        Assert.True(row.IsRunning);

        tool.Release.TrySetResult();
        await runTask;

        Assert.False(viewModel.HasRunningTools);
        Assert.False(Assert.Single(viewModel.Tools).IsRunning);
    }

    [Fact]
    public async Task HasFailure_IsTrueWhenLastRunFailed()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: false, message: "something broke");

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(viewModel.Tools);
        Assert.Equal("stub", row.ToolType);
        Assert.Equal("failed", row.LastRunStatus);
        Assert.True(row.HasFailure);
    }

    [Fact]
    public async Task HasFailure_IsFalseWhenLastRunSucceeded()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: true);

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(viewModel.Tools);
        Assert.Equal("succeeded", row.LastRunStatus);
        Assert.False(row.HasFailure);
    }

    [Fact]
    public async Task RefreshHistoryAsync_SetsLastRunStatusFromMostRecentRun()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        // Older run succeeded; newer run failed — LastRunStatus should be "failed".
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: true);
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: false);

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(viewModel.Tools);
        Assert.Equal("failed", row.LastRunStatus);
    }

    [Fact]
    public async Task RecentRuns_LoadedOnExpand_ShowsMostRecentFirst()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: true);
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: false);
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: true);

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(viewModel.Tools);
        await row.LoadRecentRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, row.RecentRuns.Count);
        // Most recent first.
        Assert.Equal("succeeded", row.RecentRuns[0].Status);
        Assert.Equal("failed", row.RecentRuns[1].Status);
        Assert.Equal("succeeded", row.RecentRuns[2].Status);
    }

    [Fact]
    public async Task RecentRuns_ShowsAllRuns_WhenMoreThanTen()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        for (var i = 0; i < 12; i++)
        {
            await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: true);
        }

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(viewModel.Tools);
        await row.LoadRecentRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(12, row.RecentRuns.Count);
    }

    [Fact]
    public async Task RecentRuns_IncludesMessageFromContent()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: false, message: "disk full");

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(viewModel.Tools);
        await row.LoadRecentRunsAsync(TestContext.Current.CancellationToken);

        var run = Assert.Single(row.RecentRuns);
        Assert.Equal("disk full", run.Message);
    }

    [Fact]
    public async Task RecentRuns_DurationIsEndMinusStart_WhenCompleted()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        var writer = new ToolExecutionResultWriter(dataAccessLayer, timeProvider);
        var handle = await writer.StartAsync(HostName, "stub", TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        await writer.CompleteAsync(handle, success: true, cancellationToken: TestContext.Current.CancellationToken);

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(viewModel.Tools);
        await row.LoadRecentRunsAsync(TestContext.Current.CancellationToken);

        var run = Assert.Single(row.RecentRuns);
        Assert.Equal(TimeSpan.FromSeconds(5), run.Duration);
    }

    [Fact]
    public async Task HasFailure_IsTrueOnViewModel_WhenAnyToolHasLastRunFailed()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: false, message: "oops");

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        Assert.True(viewModel.HasFailure);
    }

    [Fact]
    public async Task HasFailure_IsFalseOnViewModel_WhenAllRunsSucceeded()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: true);

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.HasFailure);
    }

    [Fact]
    public async Task RefreshHistoryAsync_DoesNotDuplicateRow_WhenCalledTwice()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: true);

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        Assert.Single(viewModel.Tools);
    }

    [Fact]
    public async Task ExpandCommand_TogglesIsExpanded_AndTriggersRecentRunsLoad()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: true);

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(viewModel.Tools);
        Assert.False(row.IsExpanded);
        Assert.Empty(row.RecentRuns);

        row.ExpandCommand.Execute(null);

        Assert.True(row.IsExpanded);
        Assert.Single(row.RecentRuns);
    }

    [Fact]
    public async Task ExpandCommand_CollapsesRow_WhenExecutedWhileExpanded()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: true);

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(viewModel.Tools);
        row.ExpandCommand.Execute(null);
        Assert.True(row.IsExpanded);

        row.ExpandCommand.Execute(null);
        Assert.False(row.IsExpanded);
    }
}
