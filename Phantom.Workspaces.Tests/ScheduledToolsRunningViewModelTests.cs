using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.ScheduledTools;
using Phantom.Workspaces.Testing.Gui;
using Phantom.Workspaces.Tools;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ScheduledToolsRunningViewModelTests
{
    private sealed class FixedTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 6, 17, 9, 30, 0, TimeSpan.Zero);

        public FixedTimeProvider()
        {
        }

        public FixedTimeProvider(DateTimeOffset now)
        {
            this.now = now;
        }

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
        // ExpandCommand fires LoadRecentRunsAsync as fire-and-forget. #1203 pushed the load
        // off the UI thread, so allow the load a bounded amount of time to complete before
        // asserting rather than racing an additional load.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        while (row.RecentRuns.Count == 0)
        {
            await Task.Yield();
            timeoutCts.Token.ThrowIfCancellationRequested();
        }

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

    [Fact]
    public async Task RefreshHistoryAsync_PreservesSynchronizationContext_MutatesToolsOnCapturedContext()
    {
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: true);

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);

        var observedThreadIds = new ConcurrentBag<int>();
        viewModel.Tools.CollectionChanged += (sender, args) =>
        {
            observedThreadIds.Add(Environment.CurrentManagedThreadId);
        };

        await pump.PostAsync(async () =>
        {
            await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        var singleThreadId = Assert.Single(observedThreadIds.Distinct());
        Assert.Equal(pump.ThreadId, singleThreadId);
    }

    [Fact]
    public async Task LoadRecentRunsAsync_PreservesSynchronizationContext_MutatesRecentRunsOnCapturedContext()
    {
        using var pump = new SingleThreadPump(installSynchronizationContext: true);

        // Loader that always completes on a thread-pool thread so the continuation
        // has to be marshalled back to the captured context to preserve UI-thread affinity.
        var loaderThreadIds = new ConcurrentBag<int>();
        var row = new ToolRowViewModel(
            "stub",
            HostLabel,
            async cancellationToken =>
            {
                await Task.Yield();
                await Task.Run(() => { loaderThreadIds.Add(Environment.CurrentManagedThreadId); }, cancellationToken);
                return (IReadOnlyList<RunSummaryViewModel>)new[]
                {
                    new RunSummaryViewModel(new DateTimeOffset(2026, 6, 17, 9, 30, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1), "succeeded", null),
                    new RunSummaryViewModel(new DateTimeOffset(2026, 6, 17, 9, 31, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1), "failed", "boom"),
                };
            });

        var observedThreadIds = new ConcurrentBag<int>();
        row.RecentRuns.CollectionChanged += (sender, args) =>
        {
            observedThreadIds.Add(Environment.CurrentManagedThreadId);
        };

        await pump.PostAsync(async () =>
        {
            await row.LoadRecentRunsAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        Assert.Equal(2, row.RecentRuns.Count);
        var singleObserverThread = Assert.Single(observedThreadIds.Distinct());
        Assert.Equal(pump.ThreadId, singleObserverThread);
        // Sanity: the loader really did complete off the pump thread.
        Assert.All(loaderThreadIds, id => Assert.NotEqual(pump.ThreadId, id));
    }

    [Fact]
    public async Task LoadRecentRunsAsync_WhenLoaderCompletesOffThread_DoesNotMutateCollectionOffUiThread()
    {
        using var pump = new SingleThreadPump(installSynchronizationContext: true);

        // A loader that only completes when a background thread sets its TCS.
        // This guarantees the awaiter would resume off the pump thread if
        // ConfigureAwait(false) regressed into LoadRecentRunsAsync.
        var completionThreadId = 0;
        var row = new ToolRowViewModel(
            "stub",
            HostLabel,
            async cancellationToken =>
            {
                var completion = new TaskCompletionSource<IReadOnlyList<RunSummaryViewModel>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(() =>
                {
                    completionThreadId = Environment.CurrentManagedThreadId;
                    completion.SetResult(new[]
                    {
                        new RunSummaryViewModel(
                            new DateTimeOffset(2026, 6, 17, 9, 30, 0, TimeSpan.Zero),
                            TimeSpan.FromSeconds(1),
                            "succeeded",
                            null),
                    });
                }, cancellationToken);
                return await completion.Task;
            });

        var observedThreadIds = new ConcurrentBag<int>();
        var mutationException = default(Exception);
        row.RecentRuns.CollectionChanged += (sender, args) =>
        {
            observedThreadIds.Add(Environment.CurrentManagedThreadId);
            try
            {
                // Enumerating during the notification is what PanelContainerGenerator does;
                // if the collection is being mutated from another thread this throws.
                foreach (var _ in row.RecentRuns) { }
            }
            catch (InvalidOperationException ex)
            {
                mutationException = ex;
            }
        };

        await pump.PostAsync(async () =>
        {
            await row.LoadRecentRunsAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        Assert.NotEqual(0, completionThreadId);
        Assert.NotEqual(pump.ThreadId, completionThreadId);
        Assert.Null(mutationException);
        Assert.All(observedThreadIds, id => Assert.Equal(pump.ThreadId, id));
    }

    [Fact]
    public async Task LoadRecentRunsAsync_ReloadClearsAndRepopulates_ShowsMostRecentFirst()
    {
        var callCount = 0;
        var row = new ToolRowViewModel(
            "stub",
            HostLabel,
            cancellationToken =>
            {
                callCount++;
                IReadOnlyList<RunSummaryViewModel> runs = callCount == 1
                    ? new[]
                    {
                        new RunSummaryViewModel(new DateTimeOffset(2026, 6, 17, 9, 30, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1), "succeeded", null),
                        new RunSummaryViewModel(new DateTimeOffset(2026, 6, 17, 9, 29, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1), "failed", null),
                    }
                    : new[]
                    {
                        new RunSummaryViewModel(new DateTimeOffset(2026, 6, 17, 9, 40, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1), "failed", "later"),
                        new RunSummaryViewModel(new DateTimeOffset(2026, 6, 17, 9, 30, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1), "succeeded", null),
                        new RunSummaryViewModel(new DateTimeOffset(2026, 6, 17, 9, 29, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1), "failed", null),
                    };
                return Task.FromResult(runs);
            });

        await row.LoadRecentRunsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, row.RecentRuns.Count);
        Assert.Equal("succeeded", row.RecentRuns[0].Status);

        await row.LoadRecentRunsAsync(TestContext.Current.CancellationToken);

        // Reload replaces (does not append) and preserves the loader's ordering.
        Assert.Equal(3, row.RecentRuns.Count);
        Assert.Equal("later", row.RecentRuns[0].Message);
        Assert.Equal("failed", row.RecentRuns[0].Status);
        Assert.Equal("succeeded", row.RecentRuns[1].Status);
        Assert.Equal("failed", row.RecentRuns[2].Status);
    }

    [Fact]
    public async Task RefreshHistoryAsync_ConcurrentWithRefresh_DoesNotThrow()
    {
        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        var tool = new GatedTool();
        var (dataAccessLayer, host, hostId) = await CreateHostAsync(tool);
        var timeProvider = new FixedTimeProvider();
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: true);

        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);

        var exceptionCaught = false;
        viewModel.Tools.CollectionChanged += (sender, args) =>
        {
            try
            {
                var _ = viewModel.Tools.Count;
            }
            catch (InvalidOperationException)
            {
                exceptionCaught = true;
            }
        };

        var refreshHistoryTask = pump.PostAsync(async () =>
        {
            await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runTask = host.RunDueToolsAsync(new EntityId(hostId), HostName, cts.Token);

        await tool.Started.Task;
        await Task.Delay(50, TestContext.Current.CancellationToken);
        tool.Release.TrySetResult();

        await Task.WhenAll(refreshHistoryTask, runTask);

        Assert.False(exceptionCaught, "ObservableCollection was mutated from multiple threads");
    }

    [Fact]
    public void RunSummaryViewModel_WhenRunSucceeded_ShowsCheckGlyphNotHourglass()
    {
        var run = new RunSummaryViewModel(
            new DateTimeOffset(2026, 6, 17, 9, 30, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(3),
            "succeeded",
            "Scanned 1 root(s); found 2 repositories.");

        Assert.Equal("✓", run.StatusGlyph);
        Assert.NotEqual("⏳", run.StatusGlyph);
    }

    [Fact]
    public async Task LoadRecentRunsForTool_WhenRunFailed_ShowsFailedGlyphAndMessage()
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
        Assert.Equal("failed", run.Status);
        Assert.Equal("✗", run.StatusGlyph);
        Assert.Equal("disk full", run.Message);
    }

    [Fact]
    public async Task LoadRecentRunsForTool_WhenOrphanRunningReconciled_ShowsFailedGlyphAndReconciliationMessage()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();

        // Seed an orphan running result (as a prior process would have left it).
        var orphanId = Guid.NewGuid();
        var startTime = new DateTimeOffset(2026, 6, 17, 9, 30, 0, TimeSpan.Zero);
        using (var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{orphanId}}",
              "entity-types": ["entity", "tool-execution-result"],
              "names": [["computer","this-machine","tool-executions","stub","20260617T093000000Z"]],
              "tool-name": "stub",
              "start-time": "{{startTime:O}}",
              "status": "running"
            }
            """))
        {
            var seedResult = await dataAccessLayer.UpdateAsync(new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed orphan" } },
                Changes = [new EntityChange { EntityId = new EntityId(orphanId), ConcurrencyTag = null, Data = document.RootElement.Clone(), EntityChangeMode = EntityChangeMode.Replace }],
            }, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(seedResult.EntityResults, r => r.UpdateState == UpdateState.Failed);
        }

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        await host.ReconcileOrphanRunningResultsAsync(HostName, TestContext.Current.CancellationToken);

        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(viewModel.Tools);
        await row.LoadRecentRunsAsync(TestContext.Current.CancellationToken);

        var run = Assert.Single(row.RecentRuns);
        Assert.Equal("failed", run.Status);
        Assert.Equal("✗", run.StatusGlyph);
        Assert.NotNull(run.Message);
        Assert.Contains("reconciled", run.Message!, StringComparison.OrdinalIgnoreCase);
    }

    // --- #1203: history load must run off the UI thread and only marshal the final merge ---

    private sealed class DedicatedThreadTaskScheduler : TaskScheduler, IDisposable
    {
        private readonly BlockingCollection<Task> queue = new();
        private readonly Thread thread;
        private int totalScheduled;
        private int peakPending;

        public DedicatedThreadTaskScheduler(string name)
        {
            this.thread = new Thread(() =>
            {
                this.ExecutionThreadId = Environment.CurrentManagedThreadId;
                this.ThreadStartedEvent.TrySetResult();
                foreach (var task in this.queue.GetConsumingEnumerable())
                {
                    Interlocked.Decrement(ref this.pendingCount);
                    this.TryExecuteTask(task);
                }
            }) { IsBackground = true, Name = name };
            this.thread.Start();
        }

        public TaskCompletionSource ThreadStartedEvent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ExecutionThreadId { get; private set; }
        public int TotalScheduled => Volatile.Read(ref this.totalScheduled);
        public int PeakPending => Volatile.Read(ref this.peakPending);

        private int pendingCount;

        protected override void QueueTask(Task task)
        {
            Interlocked.Increment(ref this.totalScheduled);
            var pending = Interlocked.Increment(ref this.pendingCount);
            int prev;
            do
            {
                prev = Volatile.Read(ref this.peakPending);
                if (pending <= prev) break;
            } while (Interlocked.CompareExchange(ref this.peakPending, pending, prev) != prev);
            this.queue.Add(task);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task>? GetScheduledTasks() => null;

        public void Dispose() => this.queue.CompleteAdding();
    }

    [Fact]
    public async Task RefreshHistoryAsync_LoadsQueryResults_OffTheForegroundScheduler()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        for (var i = 0; i < 20; i++)
        {
            await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: true);
        }

        using var scheduler = new DedicatedThreadTaskScheduler(nameof(RefreshHistoryAsync_LoadsQueryResults_OffTheForegroundScheduler));
        await scheduler.ThreadStartedEvent.Task;

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(
            host,
            dataAccessLayer,
            foregroundScheduler: scheduler);

        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        // Only the final MergeHistory should be scheduled on the foreground scheduler; the
        // query, JSON parsing, and dictionary building must all happen off-scheduler.
        Assert.Equal(1, scheduler.TotalScheduled);
        Assert.Single(viewModel.Tools);
    }

    [Fact]
    public async Task RefreshHistoryAsync_LargeHistory_DoesNotBlockForegroundScheduler()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        // Simulate a large recorded history across many tool types.
        for (var i = 0; i < 200; i++)
        {
            await WriteRunAsync(dataAccessLayer, timeProvider, $"tool-{i % 40}", success: i % 3 != 0);
        }

        using var scheduler = new DedicatedThreadTaskScheduler(nameof(RefreshHistoryAsync_LargeHistory_DoesNotBlockForegroundScheduler));
        await scheduler.ThreadStartedEvent.Task;

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(
            host,
            dataAccessLayer,
            foregroundScheduler: scheduler);

        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        // The foreground scheduler must not have queued O(N) items — only the final merge.
        Assert.True(scheduler.PeakPending <= 2, $"PeakPending was {scheduler.PeakPending}");
        Assert.Equal(1, scheduler.TotalScheduled);
        Assert.Equal(40, viewModel.Tools.Count);
    }

    [Fact]
    public async Task RefreshHistoryAsync_MergesFinalHistoryOnForegroundScheduler()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: true);
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub-2", success: false);

        using var scheduler = new DedicatedThreadTaskScheduler(nameof(RefreshHistoryAsync_MergesFinalHistoryOnForegroundScheduler));
        await scheduler.ThreadStartedEvent.Task;

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(
            host,
            dataAccessLayer,
            foregroundScheduler: scheduler);

        var mutationThreadIds = new ConcurrentBag<int>();
        viewModel.Tools.CollectionChanged += (_, _) =>
            mutationThreadIds.Add(Environment.CurrentManagedThreadId);

        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(mutationThreadIds);
        Assert.All(mutationThreadIds, id => Assert.Equal(scheduler.ExecutionThreadId, id));
    }

    [Fact]
    public async Task RefreshHistoryAsync_UsesInjectedForegroundScheduler_NotSynchronizationContext()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        await WriteRunAsync(dataAccessLayer, timeProvider, "stub", success: true);

        using var pump = new SingleThreadPump(installSynchronizationContext: true);
        using var scheduler = new DedicatedThreadTaskScheduler(nameof(RefreshHistoryAsync_UsesInjectedForegroundScheduler_NotSynchronizationContext));
        await scheduler.ThreadStartedEvent.Task;

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(
            host,
            dataAccessLayer,
            foregroundScheduler: scheduler);

        var mutationThreadIds = new ConcurrentBag<int>();
        viewModel.Tools.CollectionChanged += (_, _) =>
            mutationThreadIds.Add(Environment.CurrentManagedThreadId);

        await pump.PostAsync(async () =>
        {
            await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);
            return 0;
        });

        Assert.NotEmpty(mutationThreadIds);
        // Mutations must happen on the injected scheduler, not on the pump's captured context.
        Assert.All(mutationThreadIds, id => Assert.Equal(scheduler.ExecutionThreadId, id));
        Assert.All(mutationThreadIds, id => Assert.NotEqual(pump.ThreadId, id));
    }

    [Fact]
    public async Task RefreshHistoryAsync_AfterAsyncLoad_HistoryIsRenderedCorrectly()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        // Two tools; each has multiple runs so LastRunStatus reflects the most recent.
        await WriteRunAsync(dataAccessLayer, timeProvider, "tool-a", success: true);
        await WriteRunAsync(dataAccessLayer, timeProvider, "tool-b", success: true);
        await WriteRunAsync(dataAccessLayer, timeProvider, "tool-a", success: false, message: "boom");
        await WriteRunAsync(dataAccessLayer, timeProvider, "tool-b", success: true);

        using var scheduler = new DedicatedThreadTaskScheduler(nameof(RefreshHistoryAsync_AfterAsyncLoad_HistoryIsRenderedCorrectly));
        await scheduler.ThreadStartedEvent.Task;

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(
            host,
            dataAccessLayer,
            foregroundScheduler: scheduler);

        var raised = new ConcurrentBag<string>();
        viewModel.PropertyChanged += (_, args) => raised.Add(args.PropertyName ?? "");

        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, viewModel.Tools.Count);
        var toolA = Assert.Single(viewModel.Tools, t => t.ToolType == "tool-a");
        var toolB = Assert.Single(viewModel.Tools, t => t.ToolType == "tool-b");
        Assert.Equal("failed", toolA.LastRunStatus);
        Assert.Equal("succeeded", toolB.LastRunStatus);
        Assert.True(toolA.HasFailure);
        Assert.False(toolB.HasFailure);
        Assert.True(viewModel.HasFailure);
        Assert.Contains(nameof(ScheduledToolsRunningViewModel.HasRunningTools), raised);
        Assert.Contains(nameof(ScheduledToolsRunningViewModel.HasFailure), raised);
    }

    [Fact]
    public async Task LoadRecentRunsAsync_LoadsRunsOffThread_AndUpdatesRecentRunsOnForegroundScheduler()
    {
        using var scheduler = new DedicatedThreadTaskScheduler(nameof(LoadRecentRunsAsync_LoadsRunsOffThread_AndUpdatesRecentRunsOnForegroundScheduler));
        await scheduler.ThreadStartedEvent.Task;

        var loaderThreadIds = new ConcurrentBag<int>();
        var row = new ToolRowViewModel(
            "stub",
            HostLabel,
            async cancellationToken =>
            {
                await Task.Run(
                    () => loaderThreadIds.Add(Environment.CurrentManagedThreadId),
                    cancellationToken).ConfigureAwait(false);
                return (IReadOnlyList<RunSummaryViewModel>)new[]
                {
                    new RunSummaryViewModel(new DateTimeOffset(2026, 6, 17, 9, 30, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1), "succeeded", null),
                    new RunSummaryViewModel(new DateTimeOffset(2026, 6, 17, 9, 31, 0, TimeSpan.Zero), TimeSpan.FromSeconds(1), "failed", "boom"),
                };
            },
            foregroundScheduler: scheduler);

        var mutationThreadIds = new ConcurrentBag<int>();
        row.RecentRuns.CollectionChanged += (_, _) =>
            mutationThreadIds.Add(Environment.CurrentManagedThreadId);

        await row.LoadRecentRunsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, row.RecentRuns.Count);
        // Loader must have run off the foreground scheduler.
        Assert.All(loaderThreadIds, id => Assert.NotEqual(scheduler.ExecutionThreadId, id));
        // RecentRuns mutations must be marshaled onto the foreground scheduler.
        Assert.NotEmpty(mutationThreadIds);
        Assert.All(mutationThreadIds, id => Assert.Equal(scheduler.ExecutionThreadId, id));
    }

    // --- #1358: history queries must be bounded and filtered, not load the entire table ---

    private sealed class RecordingDataAccessLayer : IDataAccessLayer
    {
        private readonly IDataAccessLayer inner;

        public RecordingDataAccessLayer(IDataAccessLayer inner) => this.inner = inner;

        public List<QueryRequest> QueryRequests { get; } = new();

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        {
            this.QueryRequests.Add(request);
            return this.inner.QueryAsync(request, cancellationToken);
        }

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
            => this.inner.UpdateAsync(request, cancellationToken);

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
            => this.inner.GetAsync(request, cancellationToken);

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => this.inner.GetHistoryAsync(request, cancellationToken);

#pragma warning disable CS0618 // forwarding call to the obsolete member is intentional
        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => this.inner.ExportAsync(request, cancellationToken);
#pragma warning restore CS0618

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => this.inner.GetChangedEntitiesAsync(request, cancellationToken);
    }

    private static bool ClauseTreeContains(QueryClause clause, Func<QueryClause, bool> predicate)
    {
        if (predicate(clause))
        {
            return true;
        }

        return clause switch
        {
            TopQueryClause top => ClauseTreeContains(top.Clause, predicate),
            AndQueryClause and => and.Clauses.Any(c => ClauseTreeContains(c, predicate)),
            OrQueryClause or => or.Clauses.Any(c => ClauseTreeContains(c, predicate)),
            NotQueryClause not => ClauseTreeContains(not.Clause, predicate),
            _ => false,
        };
    }

    private static IEnumerable<QueryRequest> HistoryQueries(RecordingDataAccessLayer dal)
        => dal.QueryRequests.Where(r => r.Clauses.Any(c => c.ClauseIdentifier.Value == "tool-execution-results"));

    private static bool IsBounded(QueryRequest request)
        => request.Clauses.Any(c => ClauseTreeContains(c.Clause, cl => cl is TopQueryClause));

    private static bool FiltersToolName(QueryRequest request, string toolType)
        => request.Clauses.Any(c => ClauseTreeContains(
            c.Clause,
            cl => cl is EntityFieldQueryClause f
                && f.FieldPath.Components.SequenceEqual(new[] { "tool-name" })
                && f.ComparisonOperator == FieldComparisonOperator.Equals
                && f.Value is JsonElement v
                && v.ValueKind == JsonValueKind.String
                && v.GetString() == toolType));

    [Fact]
    public async Task RefreshHistoryAsync_WithLargeHistory_LoadsOnlyBoundedRecentWindow()
    {
        var inner = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        for (var i = 0; i < 30; i++)
        {
            await WriteRunAsync(inner, timeProvider, "stub", success: true);
        }

        var dataAccessLayer = new RecordingDataAccessLayer(inner);
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);

        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        var historyQueries = HistoryQueries(dataAccessLayer).ToArray();
        Assert.NotEmpty(historyQueries);
        Assert.All(historyQueries, r => Assert.True(IsBounded(r)));
        Assert.Single(viewModel.Tools);
    }

    [Fact]
    public async Task LoadRecentRunsForToolAsync_FiltersByToolInQuery_DoesNotMaterializeOtherToolsRuns()
    {
        var inner = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        await WriteRunAsync(inner, timeProvider, "stub", success: true);
        await WriteRunAsync(inner, timeProvider, "other-tool", success: false);

        var dataAccessLayer = new RecordingDataAccessLayer(inner);
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        var row = viewModel.Tools.Single(r => r.ToolType == "stub");
        dataAccessLayer.QueryRequests.Clear();
        await row.LoadRecentRunsAsync(TestContext.Current.CancellationToken);

        // The load query filters by tool at the query and is bounded.
        Assert.Contains(dataAccessLayer.QueryRequests, r => FiltersToolName(r, "stub"));
        Assert.All(HistoryQueries(dataAccessLayer), r => Assert.True(IsBounded(r)));

        // Only the "stub" tool's run is materialised — "other-tool" never appears.
        var run = Assert.Single(row.RecentRuns);
        Assert.Equal("succeeded", run.Status);
    }

    [Fact]
    public async Task RefreshHistoryAsync_WithLargeHistory_CompletesWithinBound()
    {
        var inner = new InMemoryDataAccessLayer();
        var timeProvider = new FixedTimeProvider();
        for (var i = 0; i < 40; i++)
        {
            await WriteRunAsync(inner, timeProvider, "stub", success: i % 2 == 0);
        }

        var dataAccessLayer = new RecordingDataAccessLayer(inner);
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);

        // Completes (returns) against a bounded query rather than an unbounded materialisation.
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        Assert.All(HistoryQueries(dataAccessLayer), r => Assert.True(IsBounded(r)));
        var row = Assert.Single(viewModel.Tools);
        Assert.NotNull(row.LastRunStatus);
    }

    // --- #1357: incremental time-windowed (~1 hour) paging of run history on scroll ---

    private static ToolRowViewModel CreateWindowedRow(
        IReadOnlyList<RunSummaryViewModel> allRuns,
        TimeProvider timeProvider,
        List<(DateTimeOffset Lower, DateTimeOffset Upper)>? requestedWindows = null,
        Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<IReadOnlyList<RunSummaryViewModel>>>? loadWindowOverride = null,
        Func<DateTimeOffset, CancellationToken, Task<bool>>? hasOlderRunsOverride = null)
    {
        Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<IReadOnlyList<RunSummaryViewModel>>> loadWindow =
            loadWindowOverride ?? ((lower, upper, _) =>
            {
                requestedWindows?.Add((lower, upper));
                IReadOnlyList<RunSummaryViewModel> page = allRuns
                    .Where(r => r.StartedAt >= lower && r.StartedAt < upper)
                    .OrderByDescending(r => r.StartedAt)
                    .ToArray();
                return Task.FromResult(page);
            });

        Func<DateTimeOffset, CancellationToken, Task<bool>> hasOlderRuns =
            hasOlderRunsOverride ?? ((upperExclusive, _) =>
                Task.FromResult(allRuns.Any(r => r.StartedAt < upperExclusive)));

        return new ToolRowViewModel(
            "stub",
            HostLabel,
            _ => Task.FromResult<IReadOnlyList<RunSummaryViewModel>>(Array.Empty<RunSummaryViewModel>()),
            foregroundScheduler: null,
            loadWindow: loadWindow,
            hasOlderRuns: hasOlderRuns,
            timeProvider: timeProvider);
    }

    private static RunSummaryViewModel RunAt(DateTimeOffset startedAt, string status = "succeeded")
        => new(startedAt, TimeSpan.FromSeconds(1), status, null);

    [Fact]
    public async Task InitialLoad_LoadsOnlyMostRecentOneHourWindow()
    {
        var timeProvider = new FixedTimeProvider();
        var now = timeProvider.GetUtcNow();
        var allRuns = new[]
        {
            RunAt(now - TimeSpan.FromMinutes(15)), // recent hour
            RunAt(now - TimeSpan.FromMinutes(45)), // recent hour
            RunAt(now - TimeSpan.FromMinutes(90)), // older hour — must be excluded
            RunAt(now - TimeSpan.FromMinutes(200)), // much older — must be excluded
        };
        var requestedWindows = new List<(DateTimeOffset Lower, DateTimeOffset Upper)>();
        var row = CreateWindowedRow(allRuns, timeProvider, requestedWindows);

        await row.LoadInitialWindowAsync(TestContext.Current.CancellationToken);

        // Only the most-recent ~1-hour window was queried, not the entire history.
        var window = Assert.Single(requestedWindows);
        Assert.Equal(now, window.Upper);
        Assert.Equal(now - TimeSpan.FromHours(1), window.Lower);
        Assert.Equal(TimeSpan.FromHours(1), window.Upper - window.Lower);

        // Only the two runs inside the most-recent hour are materialised.
        Assert.Equal(2, row.RecentRuns.Count);
        Assert.All(row.RecentRuns, r => Assert.True(r.StartedAt >= now - TimeSpan.FromHours(1)));
        Assert.Equal(now - TimeSpan.FromHours(1), row.CurrentWindowStart);
    }

    [Fact]
    public async Task LoadNextWindow_AdvancesByApproximatelyOneHour_AppendsOlderRuns()
    {
        var timeProvider = new FixedTimeProvider();
        var now = timeProvider.GetUtcNow();
        var allRuns = new[]
        {
            RunAt(now - TimeSpan.FromMinutes(15)), // hour 1
            RunAt(now - TimeSpan.FromMinutes(45)), // hour 1
            RunAt(now - TimeSpan.FromMinutes(75)), // hour 2
            RunAt(now - TimeSpan.FromMinutes(105)), // hour 2
        };
        var requestedWindows = new List<(DateTimeOffset Lower, DateTimeOffset Upper)>();
        var row = CreateWindowedRow(allRuns, timeProvider, requestedWindows);

        await row.LoadInitialWindowAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, row.RecentRuns.Count);
        var firstPage = row.RecentRuns.ToArray();
        var windowStartAfterInitial = row.CurrentWindowStart;

        await row.LoadNextWindowAsync(TestContext.Current.CancellationToken);

        // The window advanced by exactly one hour (its new upper equals the previous lower).
        Assert.Equal(2, requestedWindows.Count);
        Assert.Equal(windowStartAfterInitial, requestedWindows[1].Upper);
        Assert.Equal(TimeSpan.FromHours(1), requestedWindows[1].Upper - requestedWindows[1].Lower);
        Assert.Equal(windowStartAfterInitial - TimeSpan.FromHours(1), row.CurrentWindowStart);

        // Older runs are appended; the initial page is preserved (not cleared).
        Assert.Equal(4, row.RecentRuns.Count);
        Assert.Equal(firstPage[0], row.RecentRuns[0]);
        Assert.Equal(firstPage[1], row.RecentRuns[1]);
        Assert.All(row.RecentRuns.Skip(2), r => Assert.True(r.StartedAt < windowStartAfterInitial));
    }

    [Fact]
    public async Task LoadNextWindow_WhenHistoryExhausted_StopsPagingAndFlagsEnd()
    {
        var timeProvider = new FixedTimeProvider();
        var now = timeProvider.GetUtcNow();
        // All history lives within the two most-recent hours; nothing older than that remains.
        var allRuns = new[]
        {
            RunAt(now - TimeSpan.FromMinutes(20)), // hour 1
            RunAt(now - TimeSpan.FromMinutes(80)), // hour 2 (oldest)
        };
        var requestedWindows = new List<(DateTimeOffset Lower, DateTimeOffset Upper)>();
        var row = CreateWindowedRow(allRuns, timeProvider, requestedWindows);

        await row.LoadInitialWindowAsync(TestContext.Current.CancellationToken);
        Assert.False(row.IsEndOfHistory); // an older run (hour 2) still remains

        await row.LoadNextWindowAsync(TestContext.Current.CancellationToken);
        var windowsAfterPaging = requestedWindows.Count;
        var runsAfterPaging = row.RecentRuns.Count;

        // The oldest run has now been paged in; no older runs remain.
        Assert.True(row.IsEndOfHistory);
        Assert.Equal(2, row.RecentRuns.Count);

        // A further scroll trigger past the end of history is a no-op: no new query, no new items.
        await row.LoadNextWindowAsync(TestContext.Current.CancellationToken);
        Assert.Equal(windowsAfterPaging, requestedWindows.Count);
        Assert.Equal(runsAfterPaging, row.RecentRuns.Count);
    }

    [Fact]
    public async Task LoadNextWindow_ConcurrentScrollTriggers_DoesNotDoubleLoad()
    {
        var timeProvider = new FixedTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCount = 0;

        var row = CreateWindowedRow(
            Array.Empty<RunSummaryViewModel>(),
            timeProvider,
            loadWindowOverride: async (_, _, _) =>
            {
                Interlocked.Increment(ref loadCount);
                await gate.Task;
                return (IReadOnlyList<RunSummaryViewModel>)Array.Empty<RunSummaryViewModel>();
            },
            hasOlderRunsOverride: (_, _) => Task.FromResult(true));

        // Fire two overlapping scroll-driven loads while the first is still in flight.
        var first = row.LoadNextWindowAsync(TestContext.Current.CancellationToken);
        var second = row.LoadNextWindowAsync(TestContext.Current.CancellationToken);

        gate.SetResult();
        await Task.WhenAll(first, second);

        // The re-entrancy guard collapsed the second trigger; the window loaded exactly once.
        Assert.Equal(1, loadCount);
    }

    [Fact]
    public async Task RefreshHistoryAsync_MoreRunsThanLimit_ShowsMostRecentRuns()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostA = new[] { "computer", "host-a" };
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // 500 older successful runs — enough to fill the entire result limit by themselves.
        for (var i = 0; i < 500; i++)
        {
            await SeedRunAsync(dataAccessLayer, hostA, "stub", t0.AddSeconds(i), "succeeded");
        }

        // The single most-recent run failed; the bounded (top-N) query must not drop it.
        await SeedRunAsync(dataAccessLayer, hostA, "stub", t0.AddSeconds(100_000), "failed");

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(viewModel.Tools);
        Assert.Equal("failed", row.LastRunStatus);
    }

    [Fact]
    public async Task LoadRecentRunsForTool_ManyOtherHostRuns_StillShowsSelectedHostRuns()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostA = new[] { "computer", "host-a" };
        var hostB = new[] { "computer", "host-b" };
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // Selected host A: ten older runs...
        for (var i = 0; i < 10; i++)
        {
            await SeedRunAsync(dataAccessLayer, hostA, "stub", t0.AddSeconds(i), "succeeded");
        }

        // ...plus one very recent run so a row for host A is created from history.
        await SeedRunAsync(dataAccessLayer, hostA, "stub", t0.AddSeconds(100_000), "succeeded");

        // Other host B floods the same tool with 500 more-recent runs (filling the limit), which
        // would starve host A's older runs if the host filter were only applied in memory.
        for (var j = 0; j < 500; j++)
        {
            await SeedRunAsync(dataAccessLayer, hostB, "stub", t0.AddSeconds(1_000 + j), "succeeded");
        }

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(viewModel.Tools, r => r.Host == "computer / host-a");
        await row.LoadRecentRunsAsync(TestContext.Current.CancellationToken);

        // All eleven of host A's runs are returned despite host B's flood filling the limit.
        Assert.Equal(11, row.RecentRuns.Count);
    }

    [Fact]
    public async Task LoadWindowForTool_BusyHour_ReturnsSelectedHostRunsWithinWindow()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostA = new[] { "computer", "host-a" };
        var hostB = new[] { "computer", "host-b" };
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = t0.AddSeconds(100_000);
        var timeProvider = new FixedTimeProvider(now);

        // Host A: five runs in the older part of the [now - 1h, now) window.
        for (var i = 0; i < 5; i++)
        {
            await SeedRunAsync(dataAccessLayer, hostA, "stub", now.AddSeconds(-3_000 - i), "succeeded");
        }

        // Host A: one run AFTER the window so a row is created from history without being in-window.
        await SeedRunAsync(dataAccessLayer, hostA, "stub", now.AddSeconds(50_000), "succeeded");

        // Host B saturates the window with 500 more-recent-in-window runs (filling the limit),
        // which would starve host A's in-window runs if host filtering were only in memory.
        for (var j = 0; j < 500; j++)
        {
            await SeedRunAsync(dataAccessLayer, hostB, "stub", now.AddSeconds(-100 - j), "succeeded");
        }

        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]));
        using var viewModel = new ScheduledToolsRunningViewModel(host, dataAccessLayer, timeProvider: timeProvider);
        await viewModel.RefreshHistoryAsync(TestContext.Current.CancellationToken);

        var row = Assert.Single(viewModel.Tools, r => r.Host == "computer / host-a");
        await row.LoadInitialWindowAsync(TestContext.Current.CancellationToken);

        // Host A's five in-window runs are returned even though host B saturated the window.
        Assert.Equal(5, row.RecentRuns.Count);
    }

    /// <summary>
    /// Seeds a completed tool-execution-result entity directly (mirroring
    /// <see cref="ToolExecutionResultWriter"/>'s shape, including the queryable <c>host-label</c>)
    /// so tests can create large, precisely-timed run histories cheaply.
    /// </summary>
    private static async Task SeedRunAsync(
        IDataAccessLayer dataAccessLayer,
        IReadOnlyList<string> hostComponents,
        string toolName,
        DateTimeOffset startTime,
        string status)
    {
        var guid = Guid.NewGuid();
        var stamp = startTime.ToUniversalTime().ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        var nameComponents = hostComponents
            .Append(ToolExecutionResultWriter.ToolExecutionsSegment)
            .Append(toolName)
            .Append(stamp)
            .ToArray();
        var hostLabel = string.Join(" / ", hostComponents);
        var startIso = startTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var namesJson = JsonSerializer.Serialize(new[] { nameComponents });

        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{guid}}",
              "entity-types": ["entity", "tool-execution-result"],
              "names": {{namesJson}},
              "tool-name": {{JsonSerializer.Serialize(toolName)}},
              "host-label": {{JsonSerializer.Serialize(hostLabel)}},
              "start-time": {{JsonSerializer.Serialize(startIso)}},
              "status": {{JsonSerializer.Serialize(status)}}
            }
            """);

        var result = await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed run" } },
            Changes =
            [
                new EntityChange
                {
                    EntityId = new EntityId(guid),
                    ConcurrencyTag = null,
                    Data = document.RootElement.Clone(),
                    EntityChangeMode = EntityChangeMode.Replace,
                },
            ],
        });

        Assert.DoesNotContain(result.EntityResults, r => r.UpdateState == UpdateState.Failed);
    }
}
