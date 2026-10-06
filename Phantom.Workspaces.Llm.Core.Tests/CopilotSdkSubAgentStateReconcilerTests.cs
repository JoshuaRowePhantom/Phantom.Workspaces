using GitHub.Copilot.Rpc;
using GitHub.Copilot;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

#pragma warning disable GHCP001
public sealed class CopilotSdkSubAgentStateReconcilerTests
{
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
        Assert.False(await reconciler.ObserveEventAsync(first, _ => Task.FromResult<IReadOnlyList<SessionEvent>>([]),
            _ => { }));
        Assert.True(await reconciler.ObserveEventAsync(live,
            _ => Task.FromResult<IReadOnlyList<SessionEvent>>([]), _ => { }));
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
}
