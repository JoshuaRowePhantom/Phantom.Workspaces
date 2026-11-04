using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotSubAgentLifecycleStoreTests
{
    [Theory]
    [InlineData(false, "completed")]
    [InlineData(false, "failed")]
    [InlineData(false, "cancelled")]
    [InlineData(true, "completed")]
    [InlineData(true, "failed")]
    [InlineData(true, "cancelled")]
    public void CopilotSubAgentLifecycleStore_UnknownAfterEviction_ResumesDurableTaskEpoch(
        bool disappeared, string outcome)
    {
        var store = new CopilotSubAgentLifecycleStore();
        var time = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "working", StatusRevision: 1));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running",
            InvocationToolCallId: "call", ObservedAt: time));
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, "running",
            TaskId: "task", InvocationToolCallId: "call", ObservedAt: time)]);
        var originalGeneration = store.InvocationGeneration("call");
        store.ReplaceTasks([]);
        if (disappeared) store.RemoveSession("child", store.Generation);
        else store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "done",
            StatusRevision: 2, ExecutionBoundary: store.Generation));
        Assert.Equal("unknown", store.Project("child"));
        Assert.False(store.IsInvocationTerminal("call"));
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, "running",
            TaskId: "task", InvocationToolCallId: "call", ObservedAt: time.AddSeconds(4))]);
        var resumedGeneration = store.InvocationGeneration("call");
        Assert.True(resumedGeneration > originalGeneration);
        Assert.Equal("running", store.Project("child"));
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, "running",
            TaskId: "task", InvocationToolCallId: "call", ObservedAt: time.AddSeconds(5))]);
        Assert.Equal(resumedGeneration, store.InvocationGeneration("call"));
        Assert.False(store.CanFinishInvocation("child", "call", time.AddSeconds(3)));
        Assert.False(store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, outcome,
            InvocationToolCallId: "call", ObservedAt: time.AddSeconds(3))));
        Assert.Equal("running", store.Project("child"));
        Assert.True(store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, outcome,
            InvocationToolCallId: "call", ObservedAt: time.AddSeconds(6))));
        Assert.Equal(outcome, store.Project("child"));
    }

    [Theory]
    [InlineData(false, "sync")]
    [InlineData(true, "sync")]
    [InlineData(false, "independent-task")]
    [InlineData(true, "independent-task")]
    [InlineData(false, "newer-invocation")]
    [InlineData(true, "newer-invocation")]
    public void CopilotSubAgentLifecycleStore_AuthoritativeRetirement_PreservesCurrentWork(
        bool disappeared, string work)
    {
        var store = new CopilotSubAgentLifecycleStore();
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "working", StatusRevision: 1));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running", InvocationToolCallId: "call"));
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, "running",
            TaskId: "task", InvocationToolCallId: "call")]);
        var boundary = store.Generation;
        if (work == "newer-invocation")
        {
            store.ReplaceTasks([]);
            store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running", InvocationToolCallId: "new"));
        }
        else if (work == "independent-task")
            store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, "running",
                TaskId: "independent", InvocationToolCallId: "independent-call")]);
        if (disappeared) store.RemoveSession("child", boundary);
        else store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "done",
            StatusRevision: 2, ExecutionBoundary: boundary));
        Assert.Equal("running", store.Project("child"));
        if (work != "newer-invocation")
        {
            var generation = store.Generation;
            store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, "running",
                TaskId: "task", InvocationToolCallId: "call")]);
            Assert.Equal(generation, store.Generation);
        }
    }

    [Theory]
    [InlineData("idle", "completed")]
    [InlineData("cancelled", "cancelled")]
    [InlineData("failed", "failed")]
    public void CopilotSubAgentLifecycleStore_EvictedStableTaskResumes_FencesOldAndAcceptsFreshTerminal(string prior, string outcome)
    {
        var store = new CopilotSubAgentLifecycleStore();
        var time = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running",
            InvocationToolCallId: "call", ObservedAt: time));
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, prior,
            TaskId: "task", InvocationToolCallId: "call", ObservedAt: time.AddSeconds(1))]);
        var previous = store.InvocationGeneration("call");
        store.ReplaceTasks([]);
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, "running",
            TaskId: "task", InvocationToolCallId: "call", ObservedAt: time.AddSeconds(3))]);
        Assert.True(store.InvocationGeneration("call") > previous);
        Assert.False(store.IsInvocationTerminal("call"));
        Assert.False(store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "cancelled",
            InvocationToolCallId: "call", ObservedAt: time.AddSeconds(2))));
        Assert.Equal("running", store.Project("child"));
        Assert.True(store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, outcome,
            InvocationToolCallId: "call", ObservedAt: time.AddSeconds(4))));
        Assert.Equal(outcome, store.Project("child"));
    }

    [Theory]
    [InlineData("cancelled", false, false)]
    [InlineData("cancelled", true, false)]
    [InlineData("cancelled", false, true)]
    [InlineData("failed", false, false)]
    [InlineData("failed", true, false)]
    [InlineData("failed", false, true)]
    public void CopilotSubAgentLifecycleStore_HistoricalTerminalTasks_CannotOverwriteLatestEpoch(
        string outcome, bool reverse, bool evictLatest)
    {
        var store = new CopilotSubAgentLifecycleStore();
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running", InvocationToolCallId: "old"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "completed", InvocationToolCallId: "old"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running", InvocationToolCallId: "new"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, outcome, InvocationToolCallId: "new"));
        ApplySubAgentStateRequest old = new("child", CopilotSubAgentLifecycleLayer.Task, "completed",
            TaskId: "old-task", InvocationToolCallId: "old");
        ApplySubAgentStateRequest current = new("child", CopilotSubAgentLifecycleLayer.Task, outcome,
            TaskId: "new-task", InvocationToolCallId: "new");
        store.ReplaceTasks(evictLatest ? [old] : reverse ? [current, old] : [old, current]);
        Assert.Equal(outcome, store.Project("child"));
        store.ReplaceTasks([]);
        Assert.Equal(outcome, store.Project("child"));
    }

    [Fact]
    public void CopilotSubAgentLifecycleStore_DeliveredCancellationAfterSuccessfulSnapshot_RefinesOutcome()
    {
        var store = new CopilotSubAgentLifecycleStore();
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running", InvocationToolCallId: "call"));
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, "completed", TaskId: "task", InvocationToolCallId: "call")]);
        Assert.True(store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "cancelled", InvocationToolCallId: "call")));
        Assert.Equal("cancelled", store.Project("child"));
    }

    [Theory]
    [InlineData("cancelled", "idle")]
    [InlineData("cancelled", "completed")]
    [InlineData("failed", "idle")]
    [InlineData("failed", "completed")]
    public void CopilotSubAgentLifecycleStore_RetainedIdleSnapshot_DoesNotEraseKnownNonSuccess(string outcome, string snapshotState)
    {
        var store = new CopilotSubAgentLifecycleStore();
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running", InvocationToolCallId: "call"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, outcome, InvocationToolCallId: "call"));
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, snapshotState, TaskId: "task", InvocationToolCallId: "call")]);
        Assert.Equal(outcome, store.Project("child"));
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, "running", TaskId: "task", InvocationToolCallId: "call")]);
        Assert.Equal("running", store.Project("child"));
        Assert.False(store.IsInvocationTerminal("call"));
    }

    [Fact]
    public void CopilotSubAgentLifecycleStore_UnclosedInvocationAndAuthoritativeNonrunningEpoch_DoesNotRemainActive()
    {
        var store = new CopilotSubAgentLifecycleStore();
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running", InvocationToolCallId: "call"));
        var boundary = store.Generation;
        store.ReplaceTasks([], boundary);
        Assert.True(store.IsActive("child"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "done",
            StatusRevision: 1, ExecutionBoundary: boundary));
        Assert.False(store.IsActive("child"));
        Assert.Equal("unknown", store.Project("child"));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void CopilotSubAgentLifecycleStore_OlderCompletionAfterResumption_DoesNotStopNewEpoch(string outcome)
    {
        var store = new CopilotSubAgentLifecycleStore();
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running",
            InvocationToolCallId: "call", ObservedAt: start));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "completed",
            InvocationToolCallId: "call", ObservedAt: start.AddSeconds(1)));
        var boundary = store.Generation;
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running",
            InvocationToolCallId: "call", ObservedAt: start.AddSeconds(3)));
        Assert.False(store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, outcome,
            InvocationToolCallId: "call", ObservedAt: start.AddSeconds(2))));
        store.ReplaceTasks([], boundary);
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "done",
            StatusRevision: 1, ExecutionBoundary: boundary));
        Assert.True(store.IsActive("child"));
        Assert.Equal("running", store.Project("child"));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void CopilotSubAgentLifecycleStore_DeliveredOutcomeSurvivesEvictionAndRegistryPoll(string outcome)
    {
        var store = new CopilotSubAgentLifecycleStore();
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running", InvocationToolCallId: "call"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, outcome, InvocationToolCallId: "call"));
        store.ReplaceTasks([]);
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "done", StatusRevision: 1));
        Assert.Equal(outcome, store.Project("child"));
    }

    [Fact]
    public void CopilotSubAgentLifecycleStore_DoneThenWorkingWithNewerRevision_MarksChatActive()
    {
        var store = new CopilotSubAgentLifecycleStore();
        Assert.True(store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "done", StatusRevision: 3)));
        Assert.False(store.IsActive("child"));
        Assert.True(store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "working", StatusRevision: 4)));
        Assert.True(store.IsActive("child"));
    }

    [Fact]
    public void CopilotSubAgentLifecycleStore_OlderRevisionAfterWorking_IsRejected()
    {
        var store = new CopilotSubAgentLifecycleStore();
        Assert.True(store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "working", StatusRevision: 4)));
        Assert.False(store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "done", StatusRevision: 3)));
        Assert.True(store.IsActive("child"));
        Assert.False(store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "done")));
        Assert.True(store.IsActive("child"));
    }

    [Fact]
    public void CopilotSubAgentLifecycleStore_IdleThenRunningTask_ReactivatesSameChat()
    {
        var store = new CopilotSubAgentLifecycleStore();
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "completed", InvocationToolCallId: "call"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Task, "idle", TaskId: "task"));
        Assert.False(store.IsActive("child"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Task, "running", TaskId: "task"));
        Assert.True(store.IsActive("child"));
        Assert.True(store.IsInvocationTerminal("call"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Task, "completed", TaskId: "task"));
        Assert.False(store.IsActive("child"));
    }

    [Fact]
    public void CopilotSubAgentLifecycleStore_RunningThenIdleTask_MarksChatInactiveDespiteStaleInvocationAndSession()
    {
        var store = new CopilotSubAgentLifecycleStore();
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "waiting", StatusRevision: 1));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running", InvocationToolCallId: "call"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Task, "running", TaskId: "task", InvocationToolCallId: "call"));
        Assert.True(store.IsActive("child"));
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, "idle",
            TaskId: "task", InvocationToolCallId: "call")]);
        Assert.False(store.IsActive("child"));
        Assert.True(store.IsInvocationTerminal("call"));

        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running", InvocationToolCallId: "new"));
        Assert.True(store.IsActive("child"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "completed", InvocationToolCallId: "new"));
        Assert.False(store.IsActive("child"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Task, "running", TaskId: "other"));
        Assert.True(store.IsActive("child"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Task, "completed", TaskId: "other"));
        Assert.False(store.IsActive("child"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "waiting", StatusRevision: 2));
        Assert.False(store.IsActive("child"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "attention", StatusRevision: 3));
        Assert.False(store.IsActive("child"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "working", StatusRevision: 4));
        Assert.False(store.IsActive("child"));
    }

    [Fact]
    public void CopilotSubAgentLifecycleStore_RegistryPollAfterIdle_DoesNotReactivateUntilNewWork()
    {
        var store = new CopilotSubAgentLifecycleStore();
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "working", StatusRevision: 1));
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, "idle",
            TaskId: "task", InvocationToolCallId: "call")]);
        for (var revision = 2; revision <= 5; revision++)
        {
            store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session,
                revision % 2 == 0 ? "waiting" : "working", StatusRevision: revision));
            Assert.False(store.IsActive("child"));
        }
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Task, "running", TaskId: "task",
            InvocationToolCallId: "call"));
        Assert.True(store.IsActive("child"));
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, "idle",
            TaskId: "task", InvocationToolCallId: "call")]);
        Assert.False(store.IsActive("child"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running", InvocationToolCallId: "new"));
        Assert.True(store.IsActive("child"));
    }

    [Fact]
    public void CopilotSubAgentLifecycleStore_IdleThenEmptySnapshot_KeepsRegistrySuppressedUntilNewExecution()
    {
        var store = new CopilotSubAgentLifecycleStore();
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "working", StatusRevision: 1));
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, "idle",
            TaskId: "old", InvocationToolCallId: "old-call")]);
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "waiting", StatusRevision: 2));
        store.ReplaceTasks([]);
        Assert.False(store.IsActive("child"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "working", StatusRevision: 3));
        Assert.False(store.IsActive("child"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running", InvocationToolCallId: "fresh"));
        Assert.True(store.IsActive("child"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "completed", InvocationToolCallId: "fresh"));
        Assert.False(store.IsActive("child"));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public void CopilotSubAgentLifecycleStore_TerminalOutcomeSurvivesEmptySnapshotUntilNewExecution(string outcome)
    {
        var store = new CopilotSubAgentLifecycleStore();
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, outcome,
            TaskId: "old", InvocationToolCallId: "old-call")]);
        store.ReplaceTasks([]);
        Assert.Equal(outcome, store.LastNonrunningTaskState("child"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "working", StatusRevision: 10));
        Assert.False(store.IsActive("child"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Task, "running", TaskId: "new",
            InvocationToolCallId: "new-call"));
        Assert.Null(store.LastNonrunningTaskState("child"));
        Assert.True(store.IsActive("child"));
    }

    [Fact]
    public void CopilotSubAgentLifecycleStore_TerminalOnlySnapshot_KeepsStaleWaitingSessionSuppressed()
    {
        var store = new CopilotSubAgentLifecycleStore();
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "waiting", StatusRevision: 1));
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, "completed",
            TaskId: "old", InvocationToolCallId: "old-call")]);
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "waiting", StatusRevision: 2));
        Assert.False(store.IsActive("child"));
        store.ReplaceTasks([]);
        Assert.False(store.IsActive("child"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Task, "running", TaskId: "fresh"));
        Assert.True(store.IsActive("child"));
    }

    [Fact]
    public void CopilotSubAgentLifecycleStore_NewInvocation_PreservesTerminalHistory()
    {
        var store = new CopilotSubAgentLifecycleStore();
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "completed", InvocationToolCallId: "old"));
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running", InvocationToolCallId: "new"));
        Assert.True(store.IsActive("child"));
        Assert.True(store.IsInvocationTerminal("old"));
        Assert.False(store.IsInvocationTerminal("new"));
    }

    [Fact]
    public void CopilotSubAgentLifecycleStore_TerminalTaskSnapshot_ClosesUnreplayedInvocation()
    {
        var store = new CopilotSubAgentLifecycleStore();
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Invocation, "running", InvocationToolCallId: "call"));
        store.ReplaceTasks([new("child", CopilotSubAgentLifecycleLayer.Task, "completed",
            TaskId: "task", InvocationToolCallId: "call")]);
        Assert.True(store.IsInvocationTerminal("call"));
        Assert.False(store.IsActive("child"));
    }

    [Fact]
    public void CopilotSubAgentLifecycleStore_MissingSession_KeepsRevisionHighWaterMark()
    {
        var store = new CopilotSubAgentLifecycleStore();
        Assert.True(store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "working", StatusRevision: 8)));
        store.RemoveSession("child");
        Assert.False(store.IsActive("child"));
        Assert.False(store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "working", StatusRevision: 1)));
        Assert.False(store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "working")));
        Assert.False(store.IsActive("child"));
        Assert.True(store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "working", StatusRevision: 9)));
        Assert.True(store.IsActive("child"));
    }
}
