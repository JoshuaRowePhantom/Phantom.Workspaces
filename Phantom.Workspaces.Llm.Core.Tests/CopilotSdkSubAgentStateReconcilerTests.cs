using GitHub.Copilot.Rpc;
using GitHub.Copilot;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

#pragma warning disable GHCP001
public sealed class CopilotSdkSubAgentStateReconcilerTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task CopilotSdkSubAgentStateReconciler_EphemeralInvalidationPrecedesTerminalEvent_DoesNotLoseTerminalTransition(string outcome)
    {
        var start = new SubagentStartedEvent
        {
            Id = Guid.NewGuid(), AgentId = "child",
            Data = new() { ToolCallId = "call", AgentName = "task", AgentDisplayName = "Task", AgentDescription = "test" },
        };
        var invalidation = new SessionBackgroundTasksChangedEvent { Id = Guid.NewGuid(), ParentId = start.Id, Data = new() };
        SessionEvent terminal = outcome == "failed"
            ? new SubagentFailedEvent
            {
                Id = Guid.NewGuid(), ParentId = invalidation.Id, AgentId = "child",
                Data = new() { ToolCallId = "call", AgentName = "task", AgentDisplayName = "Task", Error = "synthetic failure" },
            }
            : new SubagentCompletedEvent
            {
                Id = Guid.NewGuid(), ParentId = invalidation.Id, AgentId = "child",
                Data = new() { ToolCallId = "call", AgentName = "task", AgentDisplayName = "Task", Cancelled = outcome == "cancelled" },
            };
        var store = new CopilotSubAgentLifecycleStore();
        var applied = new List<Guid>();
        void Apply(SessionEvent value)
        {
            applied.Add(value.Id);
            if (value is SessionBackgroundTasksChangedEvent) return;
            var state = value switch
            {
                SubagentStartedEvent => "running", SubagentFailedEvent => "failed",
                SubagentCompletedEvent e when e.Data.Cancelled == true => "cancelled", _ => "completed",
            };
            store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, state, InvocationToolCallId: "call", EventId: value.Id));
        }
        using var reconciler = new CopilotSdkSubAgentStateReconciler(
            _ => Task.FromResult<IReadOnlyList<TaskInfo>>([]), _ => store.ReplaceTasks([]));
        Task<IReadOnlyList<SessionEvent>> NoReplay(CancellationToken _) => throw new Xunit.Sdk.XunitException("A received ephemeral predecessor must not require replay.");
        Assert.False(await reconciler.ObserveEventAsync(start, NoReplay, Apply));
        Assert.False(await reconciler.ObserveEventAsync(invalidation, NoReplay, Apply));
        await reconciler.InvalidateAsync();
        Assert.True(store.IsActive("child"));
        Assert.False(await reconciler.ObserveEventAsync(terminal, NoReplay, Apply));
        Assert.Equal([start.Id, invalidation.Id, terminal.Id], applied);
        Assert.Equal(outcome, store.Project("child"));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task CopilotSdkSubAgentStateReconciler_DeferredCompletionAndEmptySnapshot_PreservesInvocationReconciliation(string outcome)
    {
        var store = new CopilotSubAgentLifecycleStore();
        var start = new SubagentStartedEvent
        {
            Id = Guid.NewGuid(), AgentId = "child",
            Data = new() { ToolCallId = "call", AgentName = "task", AgentDisplayName = "Task", AgentDescription = "test" },
        };
        var finish = new SubagentCompletedEvent
        {
            Id = Guid.NewGuid(), ParentId = Guid.NewGuid(), AgentId = "child",
            Data = new() { ToolCallId = "call", AgentName = "task", AgentDisplayName = "Task" },
        };
        void Apply(SessionEvent e) => store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation,
            e is SubagentStartedEvent ? "running" : outcome, InvocationToolCallId: "call", EventId: e.Id));
        using var reconciler = new CopilotSdkSubAgentStateReconciler(
            _ => Task.FromResult<IReadOnlyList<TaskInfo>>([]), _ => store.ReplaceTasks([]));
        await reconciler.ObserveEventAsync(start, _ => Task.FromResult<IReadOnlyList<SessionEvent>>([]), Apply);
        Assert.True(await reconciler.ObserveEventAsync(finish, _ => Task.FromResult<IReadOnlyList<SessionEvent>>([]), Apply));
        Assert.True(store.IsActive("child"));
        await reconciler.InvalidateAsync();
        Assert.False(store.IsActive("child"));
        Assert.Equal(outcome, store.Project("child"));
        await reconciler.ObserveEventAsync(finish, _ => Task.FromResult<IReadOnlyList<SessionEvent>>([]), Apply);
        Assert.Equal(outcome, store.Project("child"));
    }

    [Fact]
    public async Task CopilotSdkSubAgentStateReconciler_ParentEventGap_ReplaysAndDeduplicatesByEventId()
    {
        var first = new SessionBackgroundTasksChangedEvent { Id = Guid.NewGuid(), Data = new SessionBackgroundTasksChangedData() };
        var recovered = new SessionBackgroundTasksChangedEvent
        {
            Id = Guid.NewGuid(), ParentId = first.Id, Data = new SessionBackgroundTasksChangedData(),
        };
        var live = new SessionBackgroundTasksChangedEvent
        {
            Id = Guid.NewGuid(), ParentId = recovered.Id, Data = new SessionBackgroundTasksChangedData(),
        };
        var applied = new List<Guid>();
        var replayCount = 0;
        using var reconciler = new CopilotSdkSubAgentStateReconciler(
            _ => Task.FromResult<IReadOnlyList<TaskInfo>>([]), _ => { });
        Assert.False(await reconciler.ObserveEventAsync(first, _ => Task.FromResult<IReadOnlyList<SessionEvent>>([]),
            e => applied.Add(e.Id)));
        Assert.False(await reconciler.ObserveEventAsync(live, _ =>
        {
            replayCount++;
            return Task.FromResult<IReadOnlyList<SessionEvent>>([live, recovered, first]);
        }, e => applied.Add(e.Id)));
        Assert.Equal(1, replayCount);
        Assert.Equal([first.Id, recovered.Id, live.Id], applied);
    }

    [Fact]
    public async Task CopilotSdkSubAgentStateReconciler_EphemeralGapWithoutReplay_RequestsSnapshot()
    {
        var first = new SessionBackgroundTasksChangedEvent { Id = Guid.NewGuid(), Data = new SessionBackgroundTasksChangedData() };
        var live = new SessionBackgroundTasksChangedEvent
        {
            Id = Guid.NewGuid(), ParentId = Guid.NewGuid(), Data = new SessionBackgroundTasksChangedData(),
        };
        using var reconciler = new CopilotSdkSubAgentStateReconciler(
            _ => Task.FromResult<IReadOnlyList<TaskInfo>>([]), _ => { });
        var applied = new List<Guid>();
        Assert.False(await reconciler.ObserveEventAsync(first, _ => Task.FromResult<IReadOnlyList<SessionEvent>>([]),
            e => applied.Add(e.Id)));
        Assert.True(await reconciler.ObserveEventAsync(live,
            _ => Task.FromResult<IReadOnlyList<SessionEvent>>([]), e => applied.Add(e.Id)));
        Assert.Equal([first.Id], applied);
        Assert.False(await reconciler.ObserveEventAsync(new SessionBackgroundTasksChangedEvent
        {
            Id = live.ParentId!.Value, ParentId = first.Id, Data = new SessionBackgroundTasksChangedData(),
        }, _ => Task.FromResult<IReadOnlyList<SessionEvent>>([]), e => applied.Add(e.Id)));
        Assert.Equal([first.Id, live.ParentId.Value, live.Id], applied);
    }

    [Fact]
    public async Task CopilotSdkSubAgentStateReconciler_InvalidationDuringFetch_RefetchesNewestSnapshot()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var observed = new List<IReadOnlyList<TaskInfo>>();
        using var reconciler = new CopilotSdkSubAgentStateReconciler(async _ =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                entered.SetResult();
                await release.Task;
                return [new TaskInfoAgent { Id = "task", ToolCallId = "call", Prompt = "hello", Description = "test", AgentType = "task", StartedAt = DateTimeOffset.UtcNow, Status = GitHub.Copilot.Rpc.TaskStatus.Idle }];
            }
            return [new TaskInfoAgent { Id = "task", ToolCallId = "call", Prompt = "hello", Description = "test", AgentType = "task", StartedAt = DateTimeOffset.UtcNow, Status = GitHub.Copilot.Rpc.TaskStatus.Running }];
        }, snapshot => observed.Add(snapshot));
        var first = reconciler.InvalidateAsync();
        await entered.Task;
        var second = reconciler.InvalidateAsync();
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, count);
        Assert.Equal(GitHub.Copilot.Rpc.TaskStatus.Running, ((TaskInfoAgent)observed[^1][0]).Status);
    }

    [Fact]
    public async Task CopilotSdkSubAgentStateReconciler_UnrepairedGap_SnapshotPreservesDeferredEvent()
    {
        var first = new SessionBackgroundTasksChangedEvent { Id = Guid.NewGuid(), Data = new SessionBackgroundTasksChangedData() };
        var missingId = Guid.NewGuid();
        var stale = new SessionBackgroundTasksChangedEvent
        {
            Id = Guid.NewGuid(), ParentId = missingId, Data = new SessionBackgroundTasksChangedData(),
        };
        var applied = new List<Guid>();
        var snapshots = 0;
        using var reconciler = new CopilotSdkSubAgentStateReconciler(
            _ => Task.FromResult<IReadOnlyList<TaskInfo>>([]), _ => Interlocked.Increment(ref snapshots));
        await reconciler.ObserveEventAsync(first, _ => Task.FromResult<IReadOnlyList<SessionEvent>>([]),
            e => applied.Add(e.Id));
        Assert.True(await reconciler.ObserveEventAsync(stale, _ => Task.FromResult<IReadOnlyList<SessionEvent>>([]),
            e => applied.Add(e.Id)));
        await reconciler.InvalidateAsync();
        Assert.Equal(1, snapshots);
        Assert.Equal([first.Id, stale.Id], applied);
        var next = new SessionBackgroundTasksChangedEvent
        {
            Id = Guid.NewGuid(), ParentId = stale.Id, Data = new SessionBackgroundTasksChangedData(),
        };
        Assert.False(await reconciler.ObserveEventAsync(next, _ => Task.FromResult<IReadOnlyList<SessionEvent>>([]),
            e => applied.Add(e.Id)));
        Assert.Equal([first.Id, stale.Id, next.Id], applied);
        var late = new SessionBackgroundTasksChangedEvent
        {
            Id = missingId, ParentId = first.Id, Data = new SessionBackgroundTasksChangedData(),
        };
        Assert.True(await reconciler.ObserveEventAsync(late, _ => Task.FromResult<IReadOnlyList<SessionEvent>>([]),
            e => applied.Add(e.Id)));
        Assert.Equal([first.Id, stale.Id, next.Id], applied);
    }
}
