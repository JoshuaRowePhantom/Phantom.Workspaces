using System.Runtime.CompilerServices;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotQueueSteeringTests
{
    [Fact]
    public void CatchUpAdmissionFailure_UnsubscribesAndPreservesUnclaimedInput()
    {
        var queues = new AgentInputQueueManager();
        using var client = CreateCopilotClient(queues);
        queues.Enqueue(queues.ImmediateQueue, [Item("unclaimed")]);
        var original = new IOException("original catch-up admission failure");
        var sends = 0;
        Assert.Same(original, Assert.Throws<IOException>(() => client.SubscribeImmediateQueueSteering(
            (_, _) => { sends++; return Task.CompletedTask; }, () => throw original)));
        Assert.Single(queues.ImmediateQueue.Items);
        queues.Enqueue(queues.ImmediateQueue, [Item("next")]);
        using var fresh = client.SubscribeImmediateQueueSteering((_, _) => { sends++; return Task.CompletedTask; });
        Assert.Equal(2, sends);
        Assert.Empty(queues.ImmediateQueue.Items);
    }

    [Fact]
    public async Task SubscriptionCatchUp_ConcurrentAdditionDuringPendingDelivery_ClaimsEachInputOnce()
    {
        var queues = new AgentInputQueueManager();
        using var client = CreateCopilotClient(queues);
        queues.Enqueue(queues.ImmediateQueue, [Item("before subscription")]);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAcknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var enqueue = Task.Run(async () =>
        {
            await firstStarted.Task;
            queues.Enqueue(queues.ImmediateQueue, [Item("concurrent addition")]);
        });
        using var subscription = client.SubscribeImmediateQueueSteering((message, _) =>
        {
            sent.Enqueue(message.Prompt!);
            if (message.Prompt == "before subscription")
            {
                firstStarted.TrySetResult();
                return firstAcknowledgement.Task;
            }
            secondStarted.TrySetResult();
            return Task.CompletedTask;
        });
        try
        {
            await secondStarted.Task;
            await enqueue;
            Assert.False(firstAcknowledgement.Task.IsCompleted);
            Assert.Equal(["before subscription", "concurrent addition"], sent.ToArray());
            Assert.Empty(queues.ImmediateQueue.Items);
        }
        finally { firstAcknowledgement.TrySetResult(); }
        Assert.All(await client.WaitForSteeringDeliveriesAsync(), outcome => Assert.True(outcome.Delivered));
    }

    [Fact]
    public void RetiredSubscription_CapturedCallbackCannotSteerAfterNextTurnUnsuspendsClient()
    {
        var queues = new AgentInputQueueManager();
        using var client = CreateCopilotClient(queues);
        var oldSends = 0;
        var old = client.SubscribeImmediateQueueSteering((_, _) => { oldSends++; return Task.CompletedTask; });
        var handlers = (EventHandler<AgentInputQueueManager.QueueStateChangedEventArgs>)typeof(AgentInputQueueManager)
            .GetField("QueueStateChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(queues)!;
        old.Dispose();
        client.SteeringSuspendedForTest = false;
        queues.Enqueue(queues.ImmediateQueue, [Item("new turn only")]);
        handlers(queues, Added(queues.ImmediateQueue));
        Assert.Equal(0, oldSends);
        Assert.Single(queues.ImmediateQueue.Items);
        var freshSends = 0;
        using var fresh = client.SubscribeImmediateQueueSteering((_, _) => { freshSends++; return Task.CompletedTask; });
        Assert.Equal(1, freshSends);
        Assert.Empty(queues.ImmediateQueue.Items);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("terminal")]
    [InlineData("sealed")]
    [InlineData("stopped")]
    [InlineData("replaced")]
    [InlineData("observer")]
    public async Task ClaimedSteering_LifetimeClosesBeforeNativeStart_ReportsUndeliveredWithoutReplay(string close)
    {
        var queues = new AgentInputQueueManager();
        using var client = CreateCopilotClient(queues);
        var receipt = new CopilotSdkRootEventQueue();
        var reader = receipt.Acquire(false);
        var open = true;
        var sends = 0;
        Exception? refused = null;
        var observerFailure = new IOException("original steering projection observer failure");
        client.SteeringMessageForwarded += _ =>
        {
            if (close == "cancelled") open = false;
            else if (close == "terminal") receipt.Accept(new SessionIdleEvent { Data = new() });
            else if (close == "sealed") receipt.SealAccepted();
            else if (close == "stopped") receipt.Stop();
            else if (close == "replaced")
            {
                receipt.Accept(new SessionIdleEvent { Data = new() });
                receipt.Acquire(false);
            }
            else throw observerFailure;
        };
        var completion = new AgentInputTurnCompletion();
        using (client.SubscribeImmediateQueueSteering((_, _) =>
        {
            try { return receipt.StartSteering(reader, () => open, () => { sends++; return Task.CompletedTask; }); }
            catch (Exception failure) { refused = failure; throw; }
        }, () => open && receipt.IsReceiving(reader)))
        {
            queues.Enqueue(queues.ImmediateQueue,
                [new() { Messages = [new ChatMessage(ChatRole.User, "claimed")], TurnCompletion = completion }]);
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => completion.Settlement);
            if (close == "observer") Assert.Same(observerFailure, failure);
            else Assert.IsAssignableFrom<OperationCanceledException>(failure);
            var outcome = Assert.Single(await client.WaitForSteeringDeliveriesAsync());
            Assert.False(outcome.Delivered);
            Assert.Same(close == "observer" ? observerFailure : refused, outcome.Failure);
        }
        open = true;
        using var next = client.SubscribeImmediateQueueSteering((_, _) => { sends++; return Task.CompletedTask; });
        Assert.Equal(0, sends);
        Assert.Empty(queues.ImmediateQueue.Items);
    }

    [Fact]
    public async Task ForwardPendingImmediateMessages_ActiveRun_SendsQueuedItemWithImmediateMode()
    {
        var queues = new AgentInputQueueManager();
        using var client = CreateCopilotClient(queues);
        var sent = new TaskCompletionSource<MessageOptions>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = client.SubscribeImmediateQueueSteering((options, _) =>
        {
            sent.TrySetResult(options);
            return Task.CompletedTask;
        });

        queues.Enqueue(
            queues.ImmediateQueue,
            [new AgentInputItem { Messages = [new ChatMessage(ChatRole.User, "steer")] }]);

        var options = await sent.Task;
        Assert.Equal("immediate", options.Mode);
        Assert.Equal("steer", options.Prompt);
        Assert.Empty(queues.ImmediateQueue.Items);
    }

    [Fact]
    public void ForwardPendingImmediateMessages_QueuedOrHeldItem_DoesNotSendAsSteering()
    {
        var queues = new AgentInputQueueManager();
        var queued = Queue(AgentInputQueueImmediacy.Queue);
        var held = Queue(AgentInputQueueImmediacy.Held);
        queues.Enqueue(queued, [Item("queued")]);
        queues.Enqueue(held, [Item("held")]);
        using var client = CreateCopilotClient(queues);
        var sends = 0;

        client.ForwardPendingImmediateMessages(
            (_, _) =>
            {
                sends++;
                return Task.CompletedTask;
            },
            Added(queued));
        client.ForwardPendingImmediateMessages(
            (_, _) =>
            {
                sends++;
                return Task.CompletedTask;
            },
            Added(held));

        Assert.Equal(0, sends);
        Assert.Single(queued.Items);
        Assert.Single(held.Items);
    }

    [Fact]
    public void ForwardPendingImmediateMessages_TeardownSuspended_LeavesItemQueued()
    {
        var queues = new AgentInputQueueManager();
        using var client = CreateCopilotClient(queues);
        client.SteeringSuspendedForTest = true;
        queues.Enqueue(queues.ImmediateQueue, [Item("preserve")]);
        var sends = 0;

        client.ForwardPendingImmediateMessages(
            (_, _) =>
            {
                sends++;
                return Task.CompletedTask;
            },
            Added(queues.ImmediateQueue));

        Assert.Equal(0, sends);
        Assert.Single(queues.ImmediateQueue.Items);
    }

    [Fact]
    public async Task SteeringMessageForwarded_ConsumedItem_RecordsHistoryBeforeInternalSend()
    {
        var queues = new AgentInputQueueManager();
        using var client = CreateCopilotClient(queues);
        var order = new List<string>();
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SteeringMessageForwarded += _ => order.Add("history");
        using var subscription = client.SubscribeImmediateQueueSteering((_, _) =>
        {
            order.Add("send");
            sent.TrySetResult();
            return Task.CompletedTask;
        });

        queues.Enqueue(queues.ImmediateQueue, [Item("steer")]);

        await sent.Task;
        Assert.Equal(["history", "send"], order);
        Assert.Empty(queues.ImmediateQueue.Items);
    }

    [Fact]
    public async Task ToolResultSteeringMiddleware_NonCopilotImmediateItem_InjectsAtSupportedBoundary()
    {
        var inner = new CapturingChatClient();
        var queues = new AgentInputQueueManager();
        using var middleware = new ToolResultSteeringMiddleware(inner, queues);
        queues.Enqueue(queues.ImmediateQueue, [Item("at boundary")]);

        await middleware.GetResponseAsync(
            [new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call", "result")])],
            cancellationToken: CancellationToken.None);

        Assert.Equal(2, inner.Messages.Count);
        Assert.Equal("at boundary", inner.Messages[1].Text);
        Assert.Empty(queues.ImmediateQueue.Items);
    }

    [Fact]
    public async Task ToolResultSteeringMiddleware_NoSupportedBoundary_LeavesItemForFutureTurn()
    {
        var inner = new CapturingChatClient();
        var queues = new AgentInputQueueManager();
        using var middleware = new ToolResultSteeringMiddleware(inner, queues);
        queues.Enqueue(queues.ImmediateQueue, [Item("future")]);

        await middleware.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "continue")],
            cancellationToken: CancellationToken.None);

        Assert.Single(inner.Messages);
        Assert.Single(queues.ImmediateQueue.Items);
    }

    private static CopilotSdkChatClient CreateCopilotClient(AgentInputQueueManager queues) =>
        new(
            "gpt-5",
            "GitHub Copilot",
            gitHubToken: null,
            loggerFactory: null,
            queueManager: queues);

    private static AgentInputQueue Queue(AgentInputQueueImmediacy immediacy) =>
        new(new AgentInputQueue.Parameters { Immediacy = immediacy });

    private static AgentInputItem Item(string text) =>
        new() { Messages = [new ChatMessage(ChatRole.User, text)] };

    private static AgentInputQueueManager.QueueStateChangedEventArgs Added(AgentInputQueue queue) =>
        new()
        {
            Queue = queue,
            ChangeKind = AgentInputQueueManager.QueueStateChangeKind.ItemAdded,
        };

    private sealed class CapturingChatClient : IChatClient
    {
        internal IReadOnlyList<ChatMessage> Messages { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.Messages = messages.ToArray();
            return Task.FromResult(new ChatResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.Messages = messages.ToArray();
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
