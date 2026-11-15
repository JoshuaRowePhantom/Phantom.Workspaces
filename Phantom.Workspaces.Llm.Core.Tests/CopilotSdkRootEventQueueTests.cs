using GitHub.Copilot;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotSdkRootEventQueueTests
{
    [Theory]
    [InlineData("turn")]
    [InlineData("reasoning")]
    [InlineData("delta")]
    [InlineData("final")]
    [InlineData("call")]
    [InlineData("result")]
    public async Task EachRootOutputBoundary_StartsAnOwnedContinuation(string kind)
    {
        var queue = new CopilotSdkRootEventQueue();
        SessionEvent output = kind switch
        {
            "turn" => new AssistantTurnStartEvent { Data = new() { TurnId = "turn", InteractionId = "interaction" } },
            "reasoning" => new AssistantReasoningDeltaEvent { Data = new() { ReasoningId = "reason", DeltaContent = "thinking" } },
            "delta" => new AssistantMessageDeltaEvent { Data = new() { MessageId = "message", DeltaContent = "text" } },
            "final" => new AssistantMessageEvent { Data = new() { MessageId = "message", Content = "text" } },
            "call" => new ToolExecutionStartEvent { Data = new() { ToolCallId = "call", ToolName = "read" } },
            _ => new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "call", Success = true } },
        };
        queue.Accept(new SystemNotificationEvent { Data = new() { Content = "idle notification", Kind = new SystemNotificationAgentIdle { AgentId = "child", AgentType = "task" } } });
        Assert.False(queue.HasPending);
        queue.Accept(output);
        queue.Accept(new SessionIdleEvent { Data = new() });
        var reader = queue.Acquire(continuation: true);
        Assert.Same(output, await reader.ReadAsync());
        Assert.IsType<SessionIdleEvent>(await reader.ReadAsync());
        Assert.False(await reader.WaitToReadAsync());
        queue.Stop();
    }

    [Fact]
    public async Task DirectResponse_HasSeparateOwnershipAndCannotDiscardPendingContinuation()
    {
        var queue = new CopilotSdkRootEventQueue();
        queue.BeginDirectResponse();
        queue.Accept(new AssistantMessageEvent { Data = new() { MessageId = "direct", Content = "returned by SendAndWait" } });
        queue.Accept(new SessionIdleEvent { Data = new() });
        Assert.False(queue.HasPending);
        var accepted = new AssistantMessageEvent { Data = new() { MessageId = "autonomous", Content = "later autonomous output" } };
        queue.Accept(accepted);
        queue.Accept(new SessionIdleEvent { Data = new() });
        queue.EndDirectResponse();
        Assert.Throws<InvalidOperationException>(queue.BeginDirectResponse);
        Assert.Same(accepted, await queue.Acquire(continuation: true).ReadAsync());
        queue.Stop();
        Assert.Throws<InvalidOperationException>(queue.BeginDirectResponse);
    }

    [Fact]
    public async Task ReleasingOldTurn_DoesNotCloseNewContinuationAndStopFaultsActiveReader()
    {
        var queue = new CopilotSdkRootEventQueue();
        var old = queue.Acquire(continuation: false);
        Assert.Throws<InvalidOperationException>(queue.BeginDirectResponse);
        queue.Accept(new SessionIdleEvent { Data = new() });
        queue.Accept(new AssistantMessageEvent { Data = new() { MessageId = "new", Content = "new turn" } });
        var current = queue.Acquire(continuation: true);
        queue.Release(old);
        Assert.IsType<AssistantMessageEvent>(await current.ReadAsync());
        Assert.False(current.Completion.IsCompleted);
        queue.Stop();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await current.ReadAsync());
    }

    [Fact]
    public async Task AutonomousTurnsBeforeConsumer_AreOwnedInReceiptOrder()
    {
        var queue = new CopilotSdkRootEventQueue();
        var notifications = 0;
        queue.ContinuationAvailable += () => notifications++;
        var first = new AssistantMessageEvent { Data = new() { MessageId = "first", Content = "one" } };
        var second = new AssistantMessageEvent { Data = new() { MessageId = "second", Content = "two" } };
        queue.Accept(first);
        queue.Accept(new SessionIdleEvent { Data = new() });
        queue.Accept(second);
        queue.Accept(new SessionIdleEvent { Data = new() });
        Assert.True(queue.HasPending);
        Assert.Equal(2, notifications);
        foreach (var expected in new[] { first, second })
        {
            var events = new List<SessionEvent>();
            await foreach (var value in queue.Acquire(continuation: true).ReadAllAsync()) events.Add(value);
            Assert.Same(expected, events[0]);
            Assert.IsType<SessionIdleEvent>(events[1]);
        }
        Assert.False(queue.HasPending);
        Assert.False(await queue.Acquire(continuation: true).WaitToReadAsync());
    }

    [Fact]
    public async Task RetirementBeforeAcceptedContinuationIsConsumed_FailsWithoutDiscardingIt()
    {
        var queue = new CopilotSdkRootEventQueue();
        var accepted = new AssistantMessageEvent { Data = new() { MessageId = "accepted", Content = "accepted" } };
        queue.Accept(accepted);
        queue.Accept(new SessionIdleEvent { Data = new() });
        Assert.Throws<InvalidOperationException>(queue.Stop);
        Assert.True(queue.HasPending);
        var reader = queue.Acquire(continuation: true);
        Assert.Same(accepted, await reader.ReadAsync());
        Assert.IsType<SessionIdleEvent>(await reader.ReadAsync());
        queue.Stop();
    }

    [Fact]
    public async Task ChildOnlyActivity_DoesNotStartOrFinishParentTurn()
    {
        var queue = new CopilotSdkRootEventQueue();
        queue.Accept(new AssistantMessageDeltaEvent { AgentId = "child", Data = new() { MessageId = "child-message", DeltaContent = "child" } });
        Assert.False(queue.HasPending);
        var reader = queue.Acquire(continuation: false);
        queue.Accept(new SessionIdleEvent { AgentId = "child", Data = new() });
        Assert.IsType<SessionIdleEvent>(await reader.ReadAsync());
        Assert.False(reader.Completion.IsCompleted);
        queue.Release(reader);
        Assert.False(await reader.WaitToReadAsync());
        queue.Stop();
        queue.Accept(new AssistantMessageEvent { Data = new() { MessageId = "stale", Content = "stale" } });
        Assert.False(queue.HasPending);
        Assert.Throws<InvalidOperationException>(() => queue.Acquire(continuation: false));
    }
}
