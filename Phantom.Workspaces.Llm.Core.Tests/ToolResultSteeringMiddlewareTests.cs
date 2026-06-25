using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class ToolResultSteeringMiddlewareTests
{
    [Fact]
    public async Task NoItemsInQueue_MessageListPassedUnchanged()
    {
        var (inner, middleware, _) = CreateMiddleware();
        var messages = new List<ChatMessage> { ToolResultMessage() };

        await DrainStreamingAsync(middleware, messages);

        Assert.NotNull(inner.LastMessages);
        Assert.Single(inner.LastMessages!);
    }

    [Fact]
    public async Task ItemsInQueue_AppendedAfterFunctionResults()
    {
        var (inner, middleware, queueManager) = CreateMiddleware();
        Enqueue(queueManager, queueManager.ImmediateQueue, "steered");
        var messages = new List<ChatMessage> { ToolResultMessage() };

        await DrainStreamingAsync(middleware, messages);

        Assert.Equal(2, inner.LastMessages!.Count);
        Assert.Equal("steered", TextOf(inner.LastMessages[^1]));
    }

    [Fact]
    public async Task MultipleItemsInQueue_AllAppendedInFifoOrder()
    {
        var (inner, middleware, queueManager) = CreateMiddleware();
        Enqueue(queueManager, queueManager.ImmediateQueue, "first");
        Enqueue(queueManager, queueManager.ImmediateQueue, "second");
        var messages = new List<ChatMessage> { ToolResultMessage() };

        await DrainStreamingAsync(middleware, messages);

        Assert.Equal(3, inner.LastMessages!.Count);
        Assert.Equal("first", TextOf(inner.LastMessages[1]));
        Assert.Equal("second", TextOf(inner.LastMessages[2]));
    }

    [Fact]
    public async Task ItemsInQueue_NotInjected_WhenLastMessageIsNotToolResult()
    {
        var (inner, middleware, queueManager) = CreateMiddleware();
        Enqueue(queueManager, queueManager.ImmediateQueue, "steered");
        var messages = new List<ChatMessage> { new(ChatRole.User, "plain") };

        await DrainStreamingAsync(middleware, messages);

        Assert.Single(inner.LastMessages!);
        Assert.Single(queueManager.ImmediateQueue.Items);
    }

    [Fact]
    public async Task QueueImmediacy_ItemsNotInjected_AtToolBoundary()
    {
        var (inner, middleware, queueManager) = CreateMiddleware();
        var queuedQueue = new AgentInputQueue(new AgentInputQueue.Parameters
        {
            Immediacy = AgentInputQueueImmediacy.Queue,
        });
        Enqueue(queueManager, queuedQueue, "queued");
        var messages = new List<ChatMessage> { ToolResultMessage() };

        await DrainStreamingAsync(middleware, messages);

        Assert.Single(inner.LastMessages!);
        Assert.Single(queuedQueue.Items);
    }

    [Fact]
    public async Task HeldQueue_ItemsNotInjected()
    {
        var (inner, middleware, queueManager) = CreateMiddleware();
        var heldQueue = new AgentInputQueue(new AgentInputQueue.Parameters
        {
            Immediacy = AgentInputQueueImmediacy.Held,
        });
        Enqueue(queueManager, heldQueue, "held");
        var messages = new List<ChatMessage> { ToolResultMessage() };

        await DrainStreamingAsync(middleware, messages);

        Assert.Single(inner.LastMessages!);
        Assert.Single(heldQueue.Items);
    }

    [Fact]
    public async Task ItemsInjected_ViaGetResponseAsync()
    {
        var (inner, middleware, queueManager) = CreateMiddleware();
        Enqueue(queueManager, queueManager.ImmediateQueue, "steered");
        var messages = new List<ChatMessage> { ToolResultMessage() };

        await middleware.GetResponseAsync(messages);

        Assert.Equal(2, inner.LastMessages!.Count);
        Assert.Equal("steered", TextOf(inner.LastMessages[^1]));
    }

    [Fact]
    public async Task NoItems_ViaGetResponseAsync_Unchanged()
    {
        var (inner, middleware, _) = CreateMiddleware();
        var messages = new List<ChatMessage> { ToolResultMessage() };

        await middleware.GetResponseAsync(messages);

        Assert.Single(inner.LastMessages!);
    }

    [Fact]
    public async Task MessagesInjected_RaisedWithInjectedSteeringMessages()
    {
        var (_, middleware, queueManager) = CreateMiddleware();
        Enqueue(queueManager, queueManager.ImmediateQueue, "steered");
        var messages = new List<ChatMessage> { ToolResultMessage() };
        IReadOnlyList<ChatMessage>? raised = null;
        middleware.MessagesInjected += injected => raised = injected;

        await middleware.GetResponseAsync(messages);

        Assert.NotNull(raised);
        Assert.Single(raised!);
        Assert.Equal("steered", TextOf(raised![0]));
    }

    [Fact]
    public async Task MessagesInjected_NotRaised_WhenNoItemsInjected()
    {
        var (_, middleware, _) = CreateMiddleware();
        var messages = new List<ChatMessage> { ToolResultMessage() };
        var raisedCount = 0;
        middleware.MessagesInjected += _ => raisedCount++;

        await middleware.GetResponseAsync(messages);

        Assert.Equal(0, raisedCount);
    }

    [Fact]
    public void GetService_ReturnsSelf_ForMiddlewareType()
    {
        var (_, middleware, _) = CreateMiddleware();

        Assert.Same(middleware, middleware.GetService(typeof(ToolResultSteeringMiddleware)));
    }

    private static (CapturingChatClient Inner, ToolResultSteeringMiddleware Middleware, AgentInputQueueManager QueueManager) CreateMiddleware()
    {
        var inner = new CapturingChatClient();
        var queueManager = new AgentInputQueueManager();
        return (inner, new ToolResultSteeringMiddleware(inner, queueManager), queueManager);
    }

    private static void Enqueue(AgentInputQueueManager queueManager, AgentInputQueue queue, string text)
        => queueManager.Enqueue(queue, [new AgentInputItem { Messages = [new ChatMessage(ChatRole.User, text)] }]);

    private static ChatMessage ToolResultMessage()
        => new(ChatRole.Tool, [new FunctionResultContent("call-1", "result")]);

    private static string TextOf(ChatMessage message)
        => string.Concat(message.Contents.OfType<TextContent>().Select(content => content.Text));

    private static async Task DrainStreamingAsync(IChatClient client, IList<ChatMessage> messages)
    {
        await foreach (var _ in client.GetStreamingResponseAsync(messages))
        {
        }
    }

    private sealed class CapturingChatClient : IChatClient
    {
        public List<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.LastMessages = messages.ToList();
            return Task.FromResult(new ChatResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            this.LastMessages = messages.ToList();
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
