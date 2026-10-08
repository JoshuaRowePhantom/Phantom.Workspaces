using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotSubAgentLifecycleStoreTests
{
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
        store.Apply(new("child", CopilotSubAgentLifecycleLayer.Session, "working", StatusRevision: 2));
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
