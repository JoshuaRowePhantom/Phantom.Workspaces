using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.ScheduledTools;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ScheduledToolHostTests
{
    private sealed class FixedTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 6, 17, 9, 30, 0, TimeSpan.Zero); // Wednesday.

        public override DateTimeOffset GetUtcNow() => this.Now;
    }

    private sealed class RegistrationFailureTimeProvider : TimeProvider
    {
        private int readCount;

        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref this.readCount) == 2
            ? throw new InvalidOperationException("registration failure")
            : new DateTimeOffset(2026, 6, 17, 9, 30, 0, TimeSpan.Zero);
    }

    private sealed class RecordingTool : IWorkspaceTool
    {
        private readonly TaskCompletionSource? ran;

        public RecordingTool(string toolType = "stub", TaskCompletionSource? ran = null)
        {
            this.ToolType = toolType;
            this.ran = ran;
        }

        public int RunCount { get; private set; }

        public IReadOnlyList<EntitySnapshot> LastParticipants { get; private set; } = [];

        public EntityId CurrentComputerEntityId { get; private set; }

        public EntityId CurrentUserEntityId { get; private set; }

        public EntityId CurrentProfileEntityId { get; private set; }

        public string ToolType { get; }

        public Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
        {
            this.RunCount++;
            this.LastParticipants = context.Participants;
            this.CurrentComputerEntityId = context.CurrentComputerEntity.EntityId;
            this.CurrentUserEntityId = context.CurrentUserEntity.EntityId;
            this.CurrentProfileEntityId = context.CurrentComputerUserProfileEntity.EntityId;
            this.ran?.TrySetResult();
            return Task.FromResult(new WorkspaceToolExecutionResult());
        }
    }

    private sealed class BlockingTool : IWorkspaceTool
    {
        private readonly TaskCompletionSource started;

        public BlockingTool(TaskCompletionSource started, string toolType = "stub")
        {
            this.started = started;
            this.ToolType = toolType;
        }

        public string ToolType { get; }

        public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
        {
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = context.CancellationToken.Register(() => cancelled.TrySetCanceled(context.CancellationToken));
            this.started.SetResult();
            await cancelled.Task;
            return new WorkspaceToolExecutionResult();
        }
    }

    private sealed class CompletionControlledTool(string toolType) : IWorkspaceTool
    {
        public string ToolType { get; } = toolType;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action? CancellationCallback { get; set; }

        public bool CompleteOnCancellation { get; set; } = true;

        public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
        {
            // The source owns this registration so completion can proceed while its callback is gated.
            context.CancellationToken.Register(() =>
            {
                this.CancellationObserved.TrySetResult();
                this.CancellationCallback?.Invoke();
                if (this.CompleteOnCancellation)
                {
                    this.Complete.TrySetResult();
                }
            });
            this.Started.TrySetResult();
            await this.Complete.Task;
            context.CancellationToken.ThrowIfCancellationRequested();
            return WorkspaceToolExecutionResult.Success();
        }
    }

    private static async Task<(ScheduledToolHost Host, EntityId HostId, EntityId[] Relationships)> CreateCancellationScenarioAsync(
        CompletionControlledTool[] tools,
        TimeProvider? timeProvider = null)
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();
        await SeedScenarioAsync(dataAccessLayer, hostId, Guid.NewGuid(), Guid.NewGuid(), toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");
        await AddToolAsync(dataAccessLayer, toolId, tools[0].ToolType);
        var relationships = new List<EntityId> { new(relationshipId) };
        foreach (var tool in tools.Skip(1))
        {
            toolId = Guid.NewGuid();
            relationshipId = Guid.NewGuid();
            await AddToolAsync(dataAccessLayer, toolId, tool.ToolType);
            await AddRelationshipAsync(dataAccessLayer, relationshipId, toolId, scheduleId, hostId, tool.ToolType, paused: false);
            relationships.Add(new EntityId(relationshipId));
        }

        var host = new ScheduledToolHost(
            dataAccessLayer,
            new ScheduledToolRegistry(tools),
            resultWriter: new ToolExecutionResultWriter(dataAccessLayer, new FixedTimeProvider()),
            timeProvider: timeProvider ?? new FixedTimeProvider());
        return (host, new EntityId(hostId), relationships.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StopAllRunningExecutions_WhenSnapshotRunCompletes_RetainsSourcesUntilEveryStopExits(int stopCount)
    {
        var completingTool = new CompletionControlledTool("stub");
        var blockingTool = new CompletionControlledTool("blocking");
        var (host, hostId, relationships) = await CreateCancellationScenarioAsync([completingTool, blockingTool]);
        var disposed = new ConcurrentDictionary<EntityId, int>();
        host.ExecutionCancellationDisposed = relationship => disposed.AddOrUpdate(relationship, 1, (_, count) => count + 1);
        var firstSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSnapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var snapshotCount = 0;
        host.CancellationSnapshotAcquired = () =>
        {
            var first = Interlocked.Increment(ref snapshotCount) == 1;
            (first ? firstSnapshot : secondSnapshot).TrySetResult();
            // Only a worker thread waits here, outside host locks; the async test releases the gate.
            (first ? releaseFirst : releaseSecond).Wait(TestContext.Current.CancellationToken);
        };
        var removed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RunningExecutionsChanged += (_, _) =>
        {
            if (completingTool.Complete.Task.IsCompleted
                && !host.GetRunningExecutions().Any(run => run.RelationshipId == relationships[0]))
            {
                removed.TrySetResult();
            }
        };
        Task firstStop = Task.CompletedTask;
        Task secondStop = Task.CompletedTask;
        try
        {
            Assert.Equal(2, await host.RunDueToolsAsync(hostId, HostName, TestContext.Current.CancellationToken));
            await Task.WhenAll(completingTool.Started.Task, blockingTool.Started.Task);
            firstStop = Task.Run(host.StopAllRunningExecutions, TestContext.Current.CancellationToken);
            await firstSnapshot.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (stopCount == 2)
            {
                secondStop = Task.Run(host.StopAllRunningExecutions, TestContext.Current.CancellationToken);
                await secondSnapshot.Task.WaitAsync(TestContext.Current.CancellationToken);
            }

            completingTool.Complete.TrySetResult();
            await removed.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(host.GetRunningExecutions(), run => run.RelationshipId == relationships[0]);
            Assert.Empty(disposed);
            releaseFirst.Set();
            await firstStop;
            await blockingTool.CancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
            await host.WaitForRunningExecutionsAsync();
            Assert.Empty(host.GetRunningExecutions());
            if (stopCount == 2)
            {
                Assert.Empty(disposed);
            }

            releaseSecond.Set();
            await secondStop;
            Assert.Equal(2, disposed.Count);
            Assert.All(disposed.Values, count => Assert.Equal(1, count));
        }
        finally
        {
            host.CancellationSnapshotAcquired = null;
            releaseFirst.Set();
            releaseSecond.Set();
            completingTool.Complete.TrySetResult();
            blockingTool.Complete.TrySetResult();
            await Task.WhenAll(firstStop, secondStop);
            await host.WaitForRunningExecutionsAsync();
        }
    }

    [Fact]
    public async Task StopAllRunningExecutions_WhenCallbackReentersAndRunCompletes_DefersDisposalWithoutBlockingRemoval()
    {
        var tool = new CompletionControlledTool("stub") { CompleteOnCancellation = false };
        var (host, hostId, _) = await CreateCancellationScenarioAsync([tool]);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        var disposalCount = 0;
        host.ExecutionCancellationDisposed = _ => Interlocked.Increment(ref disposalCount);
        tool.CancellationCallback = () =>
        {
            host.StopAllRunningExecutions();
            callbackEntered.TrySetResult();
            // The callback runs on the stop worker, without host locks; completion must remain free to run.
            releaseCallback.Wait(TestContext.Current.CancellationToken);
        };
        Task stop = Task.CompletedTask;
        try
        {
            await host.RunDueToolsAsync(hostId, HostName, TestContext.Current.CancellationToken);
            await tool.Started.Task;
            stop = Task.Run(host.StopAllRunningExecutions, TestContext.Current.CancellationToken);
            await callbackEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            tool.Complete.TrySetResult();
            await host.WaitForRunningExecutionsAsync().WaitAsync(TestContext.Current.CancellationToken);
            Assert.Empty(host.GetRunningExecutions());
            Assert.Equal(0, Volatile.Read(ref disposalCount));
            releaseCallback.Set();
            await stop;
            Assert.Equal(1, disposalCount);
            host.StopAllRunningExecutions();
            Assert.Equal(1, disposalCount);
        }
        finally
        {
            releaseCallback.Set();
            tool.Complete.TrySetResult();
            await stop;
            await host.WaitForRunningExecutionsAsync();
        }
    }

    [Fact]
    public async Task StopAllRunningExecutions_WhenCallbackThrows_ReleasesUnvisitedSnapshotLeasesAndPropagatesFailure()
    {
        var first = new CompletionControlledTool("stub");
        var second = new CompletionControlledTool("second");
        var (host, hostId, _) = await CreateCancellationScenarioAsync([first, second]);
        var disposed = new ConcurrentDictionary<EntityId, int>();
        host.ExecutionCancellationDisposed = relationship => disposed.AddOrUpdate(relationship, 1, (_, count) => count + 1);
        var expected = new InvalidOperationException("cancellation callback failure");
        first.CancellationCallback = second.CancellationCallback = () => throw expected;
        host.CancellationSnapshotAcquired = () =>
        {
            first.Complete.TrySetResult();
            second.Complete.TrySetResult();
        };
        try
        {
            await host.RunDueToolsAsync(hostId, HostName, TestContext.Current.CancellationToken);
            await Task.WhenAll(first.Started.Task, second.Started.Task);
            var failure = Assert.Throws<AggregateException>(host.StopAllRunningExecutions);
            Assert.Same(expected, Assert.Single(failure.InnerExceptions));
            Assert.Single(new[] { first, second }, tool => tool.CancellationObserved.Task.IsCompleted);
            await host.WaitForRunningExecutionsAsync();
            Assert.Empty(host.GetRunningExecutions());
            Assert.Equal(2, disposed.Count);
            Assert.All(disposed.Values, count => Assert.Equal(1, count));
        }
        finally
        {
            first.Complete.TrySetResult();
            second.Complete.TrySetResult();
            await host.WaitForRunningExecutionsAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunDueToolsAsync_WhenStartupThrows_RetiresAndDisposesCancellation(bool failBeforePublication)
    {
        var tool = new CompletionControlledTool("stub");
        var (host, hostId, _) = await CreateCancellationScenarioAsync(
            [tool],
            failBeforePublication ? new RegistrationFailureTimeProvider() : null);
        var disposalCount = 0;
        host.ExecutionCancellationDisposed = _ => Interlocked.Increment(ref disposalCount);
        host.RunningExecutionsChanged += (_, _) =>
        {
            if (host.GetRunningExecutions().Count != 0)
            {
                throw new InvalidOperationException("publication notification failure");
            }
        };
        await host.RunDueToolsAsync(hostId, HostName, TestContext.Current.CancellationToken);
        await host.WaitForRunningExecutionsAsync();
        Assert.False(tool.Started.Task.IsCompleted);
        Assert.Empty(host.GetRunningExecutions());
        Assert.Equal(1, disposalCount);
    }

    private sealed class FailingTool : IWorkspaceTool
    {
        public string ToolType => "stub";

        public Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context) =>
            Task.FromResult(WorkspaceToolExecutionResult.Failure("something went wrong"));
    }

    private sealed class ThrowingTool : IWorkspaceTool
    {
        public string ToolType => "stub";

        public Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class SuccessfulTool : IWorkspaceTool
    {
        public string ToolType => "stub";

        public Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context) =>
            Task.FromResult(WorkspaceToolExecutionResult.Success() with { ResultContent = "did the thing" });
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

    private static async Task SeedScenarioAsync(
        IDataAccessLayer dataAccessLayer,
        Guid hostId,
        Guid userId,
        Guid computerId,
        Guid toolId,
        Guid scheduleId,
        Guid relationshipId,
        string scheduleRepeatJson)
    {
        await AddEntityAsync(dataAccessLayer, userId,
            $$"""{ "entity-id": "{{userId}}", "entity-types": ["entity", "user"], "names": [["users","username","test-user"]] }""");
        await AddEntityAsync(dataAccessLayer, computerId,
            $$"""{ "entity-id": "{{computerId}}", "entity-types": ["entity", "computer"], "names": [["computers","hostname","this-machine"]] }""");
        await AddEntityAsync(dataAccessLayer, hostId,
            $$"""{ "entity-id": "{{hostId}}", "entity-types": ["entity", "user-computer-profile"], "names": [["computer-user-profiles","users","username","test-user","computers","hostname","this-machine"]], "user-reference": ["users","username","test-user"], "computer-reference": ["computers","hostname","this-machine"] }""");
        await AddEntityAsync(dataAccessLayer, toolId,
            $$"""{ "entity-id": "{{toolId}}", "entity-types": ["entity", "tool"], "names": [["tools","stub"]], "tool-type": "stub" }""");
        await AddEntityAsync(dataAccessLayer, scheduleId,
            $$"""{ "entity-id": "{{scheduleId}}", "entity-types": ["entity", "schedule"], "names": [["schedule","test"]], "repeat": {{scheduleRepeatJson}} }""");
        await AddEntityAsync(dataAccessLayer, relationshipId,
            $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["entity", "tool-relationship"],
              "names": [["tool-relationships","test"]],
              "participants": { "tool": "{{toolId}}", "schedule": ["{{scheduleId}}"], "target": ["{{hostId}}"] }
            }
            """);
    }

    private static async Task AddToolAsync(
        IDataAccessLayer dataAccessLayer,
        Guid toolId,
        string toolType)
    {
        await AddEntityAsync(dataAccessLayer, toolId,
            $$"""{ "entity-id": "{{toolId}}", "entity-types": ["entity", "tool"], "names": [["tools","{{toolType}}"]], "tool-type": "{{toolType}}" }""");
    }

    private static async Task AddRelationshipAsync(
        IDataAccessLayer dataAccessLayer,
        Guid relationshipId,
        Guid toolId,
        Guid scheduleId,
        Guid hostId,
        string nameSuffix,
        bool paused)
    {
        var pausedJson = paused ? ", \"paused\": true" : string.Empty;
        await AddEntityAsync(dataAccessLayer, relationshipId,
            $$"""
            {
              "entity-id": "{{relationshipId}}",
              "entity-types": ["entity", "tool-relationship"],
              "names": [["tool-relationships","{{nameSuffix}}"]],
              "participants": { "tool": "{{toolId}}", "schedule": ["{{scheduleId}}"], "target": ["{{hostId}}"] }{{pausedJson}}
            }
            """);
    }

    [Fact]
    public async Task RunDueTools_RunsDueTool_AndRecordsResult()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        // Interval schedule that has never run -> due.
        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var tool = new RecordingTool();
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        var ranCount = await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        await host.WaitForRunningExecutionsAsync();

        Assert.Equal(1, ranCount);
        Assert.Equal(1, tool.RunCount);
        Assert.Contains(tool.LastParticipants, participant => participant.EntityId == new EntityId(hostId));
        Assert.Equal(new EntityId(computerId), tool.CurrentComputerEntityId);
        Assert.Equal(new EntityId(userId), tool.CurrentUserEntityId);
        Assert.Equal(new EntityId(hostId), tool.CurrentProfileEntityId);

        // A succeeded tool-execution-result was recorded under the host.
        var results = await QueryByTypeAsync(dataAccessLayer, "tool-execution-result");
        var resultEntity = Assert.Single(results);
        Assert.Equal("succeeded", resultEntity.GetProperty("status").GetString());
        Assert.Equal("stub", resultEntity.GetProperty("tool-name").GetString());
    }

    [Fact]
    public async Task RunDueTools_DoesNotRunToolWhoseScheduleIsNotDue()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        // Interval schedule restricted to Mondays; "now" is a Wednesday -> not an allowed day.
        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": ["monday"], "start-at": [] }""");

        var tool = new RecordingTool();
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        var ranCount = await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);

        Assert.Equal(0, ranCount);
        Assert.Equal(0, tool.RunCount);
    }

    [Fact]
    public async Task RunDueTools_IgnoresRelationshipsNotTargetingHost()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var otherHostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, otherHostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var tool = new RecordingTool();
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        // The relationship targets otherHostId, not hostId.
        var ranCount = await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);

        Assert.Equal(0, ranCount);
        Assert.Equal(0, tool.RunCount);
    }

    [Fact]
    public async Task RunDueTools_AfterRunningOnce_DoesNotRerunWhenIntervalNotElapsed()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        // Hourly interval; the fixed clock does not advance between runs.
        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "01:00:00Z", "days-of-week": [], "start-at": [] }""");

        var tool = new RecordingTool();
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        Assert.Equal(1, await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken));
        await host.WaitForRunningExecutionsAsync();
        Assert.Equal(0, await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken));
        Assert.Equal(1, tool.RunCount);
    }

    [Fact]
    public async Task RunDueTools_WhenHostPaused_DoesNotRunAnyTool()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var tool = new RecordingTool();
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        // Persist the host stop-all/pause flag on the profile entity through the real write path.
        await new ScheduledToolPauseStateService(dataAccessLayer, host).SetPausedAsync(new EntityId(hostId), paused: true, TestContext.Current.CancellationToken);

        var ranCount = await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);

        Assert.Equal(0, ranCount);
        Assert.Equal(0, tool.RunCount);
    }

    [Fact]
    public async Task RunDueTools_WhenRelationshipPaused_SkipsThatRelationshipButRunsOthers()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var runnableRelationshipId = Guid.NewGuid();
        var pausedRelationshipId = Guid.NewGuid();

        // The first relationship is runnable; add a second paused relationship bound to the same tool.
        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, runnableRelationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");
        await AddRelationshipAsync(dataAccessLayer, pausedRelationshipId, toolId, scheduleId, hostId, "paused", paused: true);

        var tool = new RecordingTool();
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        var ranCount = await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        await host.WaitForRunningExecutionsAsync();

        // Only the non-paused relationship ran.
        Assert.Equal(1, ranCount);
        Assert.Equal(1, tool.RunCount);
    }

    [Fact]
    public async Task RunDueTools_RecordsLastStartedOnRelationship_AndUsesItForDueCheck()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        // Hourly interval; the fixed clock does not advance between runs.
        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "01:00:00Z", "days-of-week": [], "start-at": [] }""");

        var tool = new RecordingTool();
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        Assert.Equal(1, await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken));
        await host.WaitForRunningExecutionsAsync();

        // The host stamped last-started on the relationship before the run.
        var relationship = Assert.Single(await QueryByTypeAsync(dataAccessLayer, "tool-relationship"));
        Assert.True(relationship.TryGetProperty("last-started", out var lastStarted));
        Assert.Equal(JsonValueKind.String, lastStarted.ValueKind);

        // The recorded last-started keeps the hourly schedule from running again at the same clock.
        Assert.Equal(0, await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken));
        Assert.Equal(1, tool.RunCount);
    }

    [Fact]
    public async Task StopAllRunningExecutions_CancelsRunningTool()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new BlockingTool(started);
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        var dispatched = await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        Assert.Equal(1, dispatched);

        // Deterministically wait until the tool has started and is registered as running.
        await started.Task;
        host.StopAllRunningExecutions();

        // Draining awaits the (now cancelled) run; the run's fault is observed inside the host.
        await host.WaitForRunningExecutionsAsync();

        // The cancelled run is no longer reported and was recorded as failed.
        Assert.Empty(host.GetRunningExecutions());
        var results = await QueryByTypeAsync(dataAccessLayer, "tool-execution-result");
        var resultEntity = Assert.Single(results);
        Assert.Equal("failed", resultEntity.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RunToolAsync_OuterCancellationToken_Cancelled_RecordsFailedResult()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new BlockingTool(started);
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        using var cts = new CancellationTokenSource();
        var dispatched = await host.RunDueToolsAsync(new EntityId(hostId), HostName, cts.Token);
        Assert.Equal(1, dispatched);

        // Wait until the tool is actually running before cancelling.
        await started.Task;
        await cts.CancelAsync();

        // The dispatched run observes cancellation; draining awaits it without throwing.
        await host.WaitForRunningExecutionsAsync();

        // The result entity must be recorded as failed, not left stuck as "running".
        var results = await QueryByTypeAsync(dataAccessLayer, "tool-execution-result");
        var resultEntity = Assert.Single(results);
        Assert.Equal("failed", resultEntity.GetProperty("status").GetString());
    }

    [Fact]
    public async Task RunToolAsync_WhenNoRegisteredToolMatchesToolType_CompletesResultHandleWithFailure()
    {
        // Regression for #1161: a seeded tool entity whose tool-type has no registered tool must
        // complete its tool-execution-result handle as failed (with a message identifying the missing
        // tool-type). It must not leave a phantom "running" result entity behind.
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        // Registry has no tools registered — the seeded "stub" tool-type will not resolve.
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([]), timeProvider: new FixedTimeProvider());

        var ranCount = await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        await host.WaitForRunningExecutionsAsync();

        // The due relationship is dispatched even though its tool-type does not resolve; the run then
        // records a failure inside RunToolAsync.
        Assert.Equal(1, ranCount);

        // A tool-execution-result must have been recorded and completed (not left "running").
        var results = await QueryByTypeAsync(dataAccessLayer, "tool-execution-result");
        var resultEntity = Assert.Single(results);
        Assert.Equal("failed", resultEntity.GetProperty("status").GetString());
        var content = resultEntity.GetProperty("content").GetProperty("default").GetProperty("content").GetProperty("text").GetString();
        Assert.NotNull(content);
        Assert.Contains("stub", content!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunDueTools_ToolReturnsFailure_RecordsFailedResult()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var tool = new FailingTool();
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        var ranCount = await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        await host.WaitForRunningExecutionsAsync();

        Assert.Equal(1, ranCount);
        var results = await QueryByTypeAsync(dataAccessLayer, "tool-execution-result");
        var resultEntity = Assert.Single(results);
        Assert.Equal("failed", resultEntity.GetProperty("status").GetString());
        Assert.Equal("something went wrong", resultEntity.GetProperty("content").GetProperty("default").GetProperty("content").GetProperty("text").GetString());
    }

    private static async Task<IReadOnlyList<JsonElement>> QueryByTypeAsync(IDataAccessLayer dataAccessLayer, string entityType)
    {
        var result = await dataAccessLayer.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("by-type"),
                    Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet([entityType]) },
                },
            ],
        });
        return result.Batches.SelectMany(batch => batch.Entities).Select(entity => entity.Data!.Value).ToArray();
    }

    [Fact]
    public async Task ScheduledToolHost_WhenToolRunBegins_LogsStartEntry()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var logger = new TestLogger<ScheduledToolHost>();
        var host = new ScheduledToolHost(
            dataAccessLayer,
            new ScheduledToolRegistry([new SuccessfulTool()]),
            timeProvider: new FixedTimeProvider(),
            logger: logger);

        await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        await host.WaitForRunningExecutionsAsync();

        Assert.Contains(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Information
            && e.Message.Contains("stub", StringComparison.Ordinal)
            && e.Message.Contains("starting", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScheduledToolHost_WhenToolRunCompletes_LogsCompletionWithSummaryAndDuration()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var logger = new TestLogger<ScheduledToolHost>();
        var host = new ScheduledToolHost(
            dataAccessLayer,
            new ScheduledToolRegistry([new SuccessfulTool()]),
            timeProvider: new FixedTimeProvider(),
            logger: logger);

        await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        await host.WaitForRunningExecutionsAsync();

        Assert.Contains(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Information
            && e.Message.Contains("completed", StringComparison.OrdinalIgnoreCase)
            && e.Message.Contains("did the thing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScheduledToolHost_WhenToolThrows_LogsErrorAndMarksRunFailed()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var logger = new TestLogger<ScheduledToolHost>();
        var host = new ScheduledToolHost(
            dataAccessLayer,
            new ScheduledToolRegistry([new ThrowingTool()]),
            timeProvider: new FixedTimeProvider(),
            logger: logger);

        await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        await host.WaitForRunningExecutionsAsync();

        Assert.Contains(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && e.Exception is InvalidOperationException
            && e.Message.Contains("threw", StringComparison.OrdinalIgnoreCase));

        var results = await QueryByTypeAsync(dataAccessLayer, "tool-execution-result");
        var resultEntity = Assert.Single(results);
        Assert.Equal("failed", resultEntity.GetProperty("status").GetString());
    }

    private sealed class CapturingExecutor : ITrustedExecutor
    {
        private readonly string targetClientInstance;

        public CapturingExecutor(string targetClientInstance) => this.targetClientInstance = targetClientInstance;

        public TrustedToolRequest? ReceivedRequest { get; private set; }

        public bool CanExecute(string targetClientInstance)
            => string.Equals(targetClientInstance, this.targetClientInstance, StringComparison.Ordinal);

        public Task<AgentChat> CreateAgentChatAsync(
            TrustedExecutionRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RunToolAsync(TrustedToolRequest request, CancellationToken cancellationToken = default)
        {
            this.ReceivedRequest = request;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RunDueTools_WithRemoteExecutor_RoutesViaExecutorInsteadOfRunningLocally()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var tool = new RecordingTool();
        var executor = new CapturingExecutor(new EntityId(hostId).ToString());
        var host = new ScheduledToolHost(
            dataAccessLayer,
            new ScheduledToolRegistry([tool]),
            executors: [executor],
            timeProvider: new FixedTimeProvider());

        var ranCount = await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        await host.WaitForRunningExecutionsAsync();

        Assert.Equal(1, ranCount);
        Assert.Equal(0, tool.RunCount);
        Assert.NotNull(executor.ReceivedRequest);
        Assert.Equal("stub", executor.ReceivedRequest!.ToolTypeName);
    }

    [Fact]
    public async Task ScheduledToolHost_WhenRunCompletes_WritesLastCompletedAndLastStatusOnRelationship()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var host = new ScheduledToolHost(
            dataAccessLayer,
            new ScheduledToolRegistry([new SuccessfulTool()]),
            timeProvider: new FixedTimeProvider());

        await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        await host.WaitForRunningExecutionsAsync();

        var relationship = Assert.Single(await QueryByTypeAsync(dataAccessLayer, "tool-relationship"));
        Assert.True(relationship.TryGetProperty("last-completed", out var lastCompleted));
        Assert.Equal(JsonValueKind.String, lastCompleted.ValueKind);
        Assert.True(DateTimeOffset.TryParse(lastCompleted.GetString(), out _));
        Assert.Equal("succeeded", relationship.GetProperty("last-status").GetString());
    }

    [Fact]
    public async Task ScheduledToolHost_WhenRunFaults_WritesLastCompletedAndLastFailedStatusOnRelationship()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var host = new ScheduledToolHost(
            dataAccessLayer,
            new ScheduledToolRegistry([new FailingTool()]),
            timeProvider: new FixedTimeProvider());

        await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        await host.WaitForRunningExecutionsAsync();

        var relationship = Assert.Single(await QueryByTypeAsync(dataAccessLayer, "tool-relationship"));
        Assert.True(relationship.TryGetProperty("last-completed", out var lastCompleted));
        Assert.Equal(JsonValueKind.String, lastCompleted.ValueKind);
        Assert.Equal("failed", relationship.GetProperty("last-status").GetString());
    }

    private sealed class ThrowingResultWriter : ToolExecutionResultWriter
    {
        public bool ThrowOnComplete { get; set; } = true;

        public ThrowingResultWriter(IDataAccessLayer dataAccessLayer, TimeProvider timeProvider)
            : base(dataAccessLayer, timeProvider)
        {
        }

        public override Task CompleteAsync(
            ToolExecutionResultHandle handle,
            bool success,
            string? content = null,
            CancellationToken cancellationToken = default)
        {
            if (this.ThrowOnComplete)
            {
                throw new InvalidOperationException("DAL boom");
            }

            return base.CompleteAsync(handle, success, content, cancellationToken);
        }
    }

    [Fact]
    public async Task ScheduledToolHost_WhenCompleteAsyncThrows_LogsErrorWithoutMaskingRunOutcome()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var logger = new TestLogger<ScheduledToolHost>();
        var throwingWriter = new ThrowingResultWriter(dataAccessLayer, new FixedTimeProvider());
        var host = new ScheduledToolHost(
            dataAccessLayer,
            new ScheduledToolRegistry([new SuccessfulTool()]),
            resultWriter: throwingWriter,
            timeProvider: new FixedTimeProvider(),
            logger: logger);

        // The tool ran successfully. CompleteAsync throwing must not mask that: the run outcome
        // (the tool's success log line) is still emitted, and the failure is logged as its own
        // Error entry — the outer catch is not re-entered.
        await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        await host.WaitForRunningExecutionsAsync();

        Assert.Contains(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Information
            && e.Message.Contains("completed", StringComparison.OrdinalIgnoreCase)
            && e.Message.Contains("did the thing", StringComparison.Ordinal));

        Assert.Contains(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && e.Exception is InvalidOperationException
            && e.Message.Contains("Failed to record completion", StringComparison.OrdinalIgnoreCase));

        // The outer catch's "threw after" log must not fire — the completion-write failure was
        // isolated from the tool's success outcome.
        Assert.DoesNotContain(logger.Entries, e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Error
            && e.Message.Contains("threw after", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScheduledToolHost_OnStartup_ReconcilesOrphanRunningResultsAsFailed()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();

        // Seed a per-run tool-execution-result entity in "running" state (as a prior process would
        // have left it): no end-time, status: "running", name path under the host.
        var startTime = new DateTimeOffset(2026, 6, 17, 9, 30, 0, TimeSpan.Zero);
        var orphanId = Guid.NewGuid();
        await AddEntityAsync(dataAccessLayer, orphanId,
            $$"""
            {
              "entity-id": "{{orphanId}}",
              "entity-types": ["entity", "tool-execution-result"],
              "names": [["computer","this-machine","tool-executions","stub","20260617T093000000Z"]],
              "tool-name": "stub",
              "start-time": "{{startTime:O}}",
              "status": "running"
            }
            """);

        var host = new ScheduledToolHost(
            dataAccessLayer,
            new ScheduledToolRegistry([]),
            timeProvider: new FixedTimeProvider());

        var reconciled = await host.ReconcileOrphanRunningResultsAsync(HostName, TestContext.Current.CancellationToken);

        Assert.Equal(1, reconciled);
        var results = await QueryByTypeAsync(dataAccessLayer, "tool-execution-result");
        var entity = Assert.Single(results);
        Assert.Equal("failed", entity.GetProperty("status").GetString());
        Assert.True(entity.TryGetProperty("end-time", out _));
        var message = entity.GetProperty("content").GetProperty("default").GetProperty("content").GetProperty("text").GetString();
        Assert.NotNull(message);
        Assert.Contains("reconciled", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunDueToolsAsync_WhenOneToolBlocksIndefinitely_StillRunsOtherDueTools()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var blockingToolId = Guid.NewGuid();
        var recordingToolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var blockingRelationshipId = Guid.NewGuid();
        var recordingRelationshipId = Guid.NewGuid();

        // Two due relationships sharing one schedule: the first tool blocks forever, the second records.
        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, blockingToolId, scheduleId, blockingRelationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");
        await AddToolAsync(dataAccessLayer, recordingToolId, "recording");
        await AddRelationshipAsync(dataAccessLayer, recordingRelationshipId, recordingToolId, scheduleId, hostId, "recording", paused: false);

        var blockingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recordingRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingTool = new BlockingTool(blockingStarted); // tool-type "stub"
        var recordingTool = new RecordingTool("recording", recordingRan);
        var host = new ScheduledToolHost(
            dataAccessLayer,
            new ScheduledToolRegistry([blockingTool, recordingTool]),
            timeProvider: new FixedTimeProvider());

        var dispatched = await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);

        // Both due tools are dispatched even though the first never returns.
        Assert.Equal(2, dispatched);
        await blockingStarted.Task;

        // The second tool runs to completion despite the first blocking indefinitely (no starvation).
        await recordingRan.Task;
        Assert.Equal(1, recordingTool.RunCount);

        // Clean up the still-blocking run.
        host.StopAllRunningExecutions();
        await host.WaitForRunningExecutionsAsync();
    }

    [Fact]
    public async Task RunDueToolsAsync_WhenToolStillRunning_DoesNotDispatchSameRelationshipTwice()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new BlockingTool(started);
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        var firstTick = await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        Assert.Equal(1, firstTick);
        await started.Task;

        // While the first run is still in flight, a second tick must not dispatch the same relationship.
        var secondTick = await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        Assert.Equal(0, secondTick);
        Assert.Single(host.GetRunningExecutions());

        host.StopAllRunningExecutions();
        await host.WaitForRunningExecutionsAsync();
    }

    [Fact]
    public async Task RunDueToolsAsync_WhenToolBlocksIndefinitely_ReturnsWithoutAwaitingCompletion()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var relationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, toolId, scheduleId, relationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new BlockingTool(started);
        var host = new ScheduledToolHost(dataAccessLayer, new ScheduledToolRegistry([tool]), timeProvider: new FixedTimeProvider());

        // RunDueToolsAsync returns after dispatch, so this await completes even though the tool never does.
        var dispatched = await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        Assert.Equal(1, dispatched);

        // The dispatched tool is still running after RunDueToolsAsync already returned.
        await started.Task;
        Assert.Single(host.GetRunningExecutions());

        host.StopAllRunningExecutions();
        await host.WaitForRunningExecutionsAsync();
    }

    [Fact]
    public async Task GetRunningExecutions_WhenMultipleToolsRunning_ReflectsAllConcurrentRuns()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var firstToolId = Guid.NewGuid();
        var secondToolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var firstRelationshipId = Guid.NewGuid();
        var secondRelationshipId = Guid.NewGuid();

        // Two due relationships, each bound to a distinct blocking tool.
        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, firstToolId, scheduleId, firstRelationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");
        await AddToolAsync(dataAccessLayer, secondToolId, "blocking-2");
        await AddRelationshipAsync(dataAccessLayer, secondRelationshipId, secondToolId, scheduleId, hostId, "blocking-2", paused: false);

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTool = new BlockingTool(firstStarted); // tool-type "stub"
        var secondTool = new BlockingTool(secondStarted, "blocking-2");
        var host = new ScheduledToolHost(
            dataAccessLayer,
            new ScheduledToolRegistry([firstTool, secondTool]),
            timeProvider: new FixedTimeProvider());

        var dispatched = await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken);
        Assert.Equal(2, dispatched);

        await firstStarted.Task;
        await secondStarted.Task;

        // Both concurrently-running tools are reflected.
        Assert.Equal(2, host.GetRunningExecutions().Count);

        host.StopAllRunningExecutions();
        await host.WaitForRunningExecutionsAsync();
    }

    [Fact]
    public async Task StopAllRunningExecutions_WhenMultipleToolsRunning_CancelsAll()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var hostId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var computerId = Guid.NewGuid();
        var firstToolId = Guid.NewGuid();
        var secondToolId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var firstRelationshipId = Guid.NewGuid();
        var secondRelationshipId = Guid.NewGuid();

        await SeedScenarioAsync(dataAccessLayer, hostId, userId, computerId, firstToolId, scheduleId, firstRelationshipId,
            """{ "frequency": "00:00:01Z", "days-of-week": [], "start-at": [] }""");
        await AddToolAsync(dataAccessLayer, secondToolId, "blocking-2");
        await AddRelationshipAsync(dataAccessLayer, secondRelationshipId, secondToolId, scheduleId, hostId, "blocking-2", paused: false);

        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTool = new BlockingTool(firstStarted); // tool-type "stub"
        var secondTool = new BlockingTool(secondStarted, "blocking-2");
        var host = new ScheduledToolHost(
            dataAccessLayer,
            new ScheduledToolRegistry([firstTool, secondTool]),
            timeProvider: new FixedTimeProvider());

        Assert.Equal(2, await host.RunDueToolsAsync(new EntityId(hostId), HostName, TestContext.Current.CancellationToken));
        await firstStarted.Task;
        await secondStarted.Task;

        host.StopAllRunningExecutions();
        await host.WaitForRunningExecutionsAsync();

        // Every concurrently-running tool was cancelled: none remain running and both recorded a failed
        // result (each blocking tool threw OperationCanceledException on cancellation).
        Assert.Empty(host.GetRunningExecutions());
        var results = await QueryByTypeAsync(dataAccessLayer, "tool-execution-result");
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal("failed", result.GetProperty("status").GetString()));
    }
}
