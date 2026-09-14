using System.Runtime.CompilerServices;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotQueueSteeringTests
{
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
