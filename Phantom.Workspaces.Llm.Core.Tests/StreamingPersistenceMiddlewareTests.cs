using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Core.Tests;

public class StreamingPersistenceMiddlewareTests
{
    private const string AgentSessionId = "test-session-id";

    [Fact]
    public async Task SingleUpdate_FinishReason_PersistedOnStreamEnd()
    {
        var spyStore = new SpyAgentPersistenceStore();
        var (middleware, session) = CreateMiddleware(spyStore, [
            MakeUpdate("hello", finishReason: "stop"),
        ]);

        var updates = await ConsumeAsync(middleware, session);

        Assert.Single(updates);
        Assert.Single(spyStore.StoredMessages);
        Assert.Equal("hello", GetText(spyStore.StoredMessages[0]));
    }

    [Fact]
    public async Task TwoUpdates_FirstPersistedBeforeSecondYielded()
    {
        // Two updates forming two separate messages via a role transition:
        //   update[0] = assistant FunctionCallContent  (no finish reason)
        //   update[1] = tool FunctionResultContent     (no finish reason)
        //
        // After buffering update[0]: 1 message, stableCount = max(0, 1-1) = 0 → nothing persisted.
        // After buffering update[1]: 2 messages, stableCount = max(0, 2-1) = 1 → message[0]
        //   is persisted BEFORE update[1] is yielded to the consumer.
        var spyStore = new SpyAgentPersistenceStore();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingClient = new GatingChatClient(gate, [
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("c1", "tool", null)] },
            new ChatResponseUpdate { Role = ChatRole.Tool, Contents = [new FunctionResultContent("c1", "result")] },
        ]);

        var provider = CreateProvider(spyStore);
        var inner = new StreamingPersistenceMiddleware(blockingClient, provider, spyStore);
        var frameworkSession = CreateSession(provider);
        inner.SetCurrentSession(frameworkSession);

        var enumerator = inner.GetStreamingResponseAsync([], null, CancellationToken.None)
            .GetAsyncEnumerator(CancellationToken.None);

        // Consume update[0] — 1 message, stableCount = 0, nothing persisted yet.
        await enumerator.MoveNextAsync();
        Assert.Empty(spyStore.StoredMessages);

        // Release gate and consume update[1] — now 2 messages, stableCount = 1.
        // message[0] (the function call) is persisted BEFORE update[1] is yielded.
        gate.SetResult();
        await enumerator.MoveNextAsync();
        Assert.Single(spyStore.StoredMessages);
        Assert.Equal(ChatRole.Assistant, spyStore.StoredMessages[0].Role);
        Assert.Contains(spyStore.StoredMessages[0].Contents, c => c is FunctionCallContent fc && fc.CallId == "c1");

        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task NUpdates_EachPersistedExactlyOnce()
    {
        // Four updates forming four distinct messages via alternating role transitions:
        //   u0: assistant FunctionCallContent("c0")   → message[0]
        //   u1: tool    FunctionResultContent("c0")   → message[1]
        //   u2: assistant FunctionCallContent("c1")   → message[2]
        //   u3: tool    FunctionResultContent("c1")   → message[3]  (finish reason)
        //
        // Persistence cadence:
        //   after u1: stableCount=1 → persist message[0]
        //   after u2: stableCount=2 → persist message[1]
        //   after u3: stableCount=4 → persist messages[2,3]
        //
        // Total stored messages = 4, each appearing exactly once.
        const int n = 4;
        var spyStore = new SpyAgentPersistenceStore();
        var updates = new ChatResponseUpdate[]
        {
            new() { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("c0", "tool", null)] },
            new() { Role = ChatRole.Tool,      Contents = [new FunctionResultContent("c0", "r0")] },
            new() { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("c1", "tool", null)] },
            new() { Role = ChatRole.Tool,      Contents = [new FunctionResultContent("c1", "r1")], FinishReason = ChatFinishReason.Stop },
        };

        var (middleware, session) = CreateMiddleware(spyStore, updates);
        await ConsumeAsync(middleware, session);

        Assert.Equal(n, spyStore.StoredMessages.Count);
        Assert.Equal(ChatRole.Assistant, spyStore.StoredMessages[0].Role);
        Assert.Equal(ChatRole.Tool,      spyStore.StoredMessages[1].Role);
        Assert.Equal(ChatRole.Assistant, spyStore.StoredMessages[2].Role);
        Assert.Equal(ChatRole.Tool,      spyStore.StoredMessages[3].Role);
    }

    [Fact]
    public async Task AllUpdatesYieldedToConsumer_Regardless()
    {
        const int n = 5;
        var spyStore = new SpyAgentPersistenceStore();
        var updates = Enumerable.Range(0, n)
            .Select(i => MakeUpdate($"msg-{i}", finishReason: i == n - 1 ? "stop" : null))
            .ToArray();

        var (middleware, session) = CreateMiddleware(spyStore, updates);
        var received = await ConsumeAsync(middleware, session);

        Assert.Equal(n, received.Count);
        for (var i = 0; i < n; i++)
        {
            Assert.Equal($"msg-{i}", string.Concat(received[i].Contents.OfType<TextContent>().Select(c => c.Text)));
        }
    }

    [Fact]
    public async Task EmptyStream_NoPersistenceCalls()
    {
        var spyStore = new SpyAgentPersistenceStore();
        var (middleware, session) = CreateMiddleware(spyStore, []);

        await ConsumeAsync(middleware, session);

        Assert.Equal(0, spyStore.StoreCallCount);
    }

    [Fact]
    public async Task GetService_PropagatesInward()
    {
        var spyStore = new SpyAgentPersistenceStore();
        var innerClient = new ServiceExposingChatClient();
        var provider = CreateProvider(spyStore);
        var middleware = new StreamingPersistenceMiddleware(innerClient, provider, spyStore);

        var result = middleware.GetService(typeof(ServiceExposingChatClient));

        Assert.Same(innerClient, result);
    }

    [Fact]
    public void GetService_ReturnsSelf()
    {
        var spyStore = new SpyAgentPersistenceStore();
        var provider = CreateProvider(spyStore);
        var middleware = new StreamingPersistenceMiddleware(new EmptyChatClient(), provider, spyStore);

        var result = middleware.GetService(typeof(StreamingPersistenceMiddleware));

        Assert.Same(middleware, result);
    }

    [Fact]
    public async Task SetCurrentSession_UsedForStoreWrites()
    {
        var spyStore = new SpyAgentPersistenceStore();
        var (middleware, session) = CreateMiddleware(spyStore, [
            MakeUpdate("text", finishReason: "stop"),
        ]);

        await ConsumeAsync(middleware, session);

        Assert.Single(spyStore.StoredAgents);
        Assert.Equal(AgentSessionId, spyStore.StoredAgents[0].AgentSessionId);
    }

    [Fact]
    public async Task CancellationOfConsumer_DoesNotAbortInFlightPersistence()
    {
        // Arrange: three role-alternating updates forming three messages.
        //   u0: assistant FunctionCallContent("c0") → message[0]
        //   u1: tool    FunctionResultContent("c0") → message[1]   (stableCount=1 after u1 → message[0] persisted)
        //   u2: assistant text "final" (will not arrive — consumer cancels first)
        //
        // When u1 is processed by the middleware, message[0] is stable and is persisted using
        // CancellationToken.None. The consumer then cancels its token. The next MoveNextAsync
        // on the inner enumerator throws OperationCanceledException. Message[0] is already in
        // the store and must not be removed by the cancellation.
        var spyStore = new SpyAgentPersistenceStore();
        var updates = new ChatResponseUpdate[]
        {
            new() { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("c0", "tool", null)] },
            new() { Role = ChatRole.Tool,      Contents = [new FunctionResultContent("c0", "result")] },
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("final")], FinishReason = ChatFinishReason.Stop },
        };

        var provider = CreateProvider(spyStore);
        var inner = new StaticChatClient(updates);
        var middleware = new StreamingPersistenceMiddleware(inner, provider, spyStore);
        var session = CreateSession(provider);
        middleware.SetCurrentSession(session);

        using var cts = new CancellationTokenSource();

        var enumerator = middleware.GetStreamingResponseAsync([], null, cts.Token)
            .GetAsyncEnumerator(cts.Token);

        // Consume u0 — 1 message, stableCount = 0, nothing persisted yet.
        await enumerator.MoveNextAsync();
        Assert.Equal(0, spyStore.StoreCallCount);

        // Consume u1 — 2 messages, stableCount = 1; message[0] is persisted with CancellationToken.None.
        await enumerator.MoveNextAsync();
        Assert.Single(spyStore.StoredMessages);

        // Cancel the consumer token — the already-persisted message must remain in the store.
        await cts.CancelAsync();

        try
        {
            // Next iteration respects cancellationToken in StaticChatClient and throws.
            await enumerator.MoveNextAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            await enumerator.DisposeAsync();
        }

        // message[0] (FunctionCallContent) is still in the store: CancellationToken.None means
        // StoreAsync cannot be aborted retroactively by the consumer's cancellation token.
        Assert.Single(spyStore.StoredMessages);
        Assert.Equal(ChatRole.Assistant, spyStore.StoredMessages[0].Role);
        Assert.Contains(spyStore.StoredMessages[0].Contents, c => c is FunctionCallContent fc && fc.CallId == "c0");
    }

    [Fact]
    public async Task NullStore_NoException()
    {
        var nullStore = NullAgentPersistenceStore.Instance;
        var (middleware, session) = CreateMiddleware(nullStore, [
            MakeUpdate("hello", finishReason: "stop"),
        ]);

        // Should complete without throwing.
        await ConsumeAsync(middleware, session);
    }

    [Fact]
    public async Task PersistMessage_WhenCreatedAtIsNull_SetsCreatedAtBeforeStoring()
    {
        var spyStore = new SpyAgentPersistenceStore();
        var (middleware, session) = CreateMiddleware(spyStore, [
            MakeUpdate("hello", finishReason: "stop"),
        ]);

        await ConsumeAsync(middleware, session);

        var message = Assert.Single(spyStore.StoredMessages);
        Assert.NotNull(message.CreatedAt);
    }

    [Fact]
    public async Task PersistMessage_WhenCreatedAtIsSet_PreservesOriginalValue()
    {
        var expected = new DateTimeOffset(2026, 7, 1, 12, 30, 0, TimeSpan.Zero);
        var spyStore = new SpyAgentPersistenceStore();
        var (middleware, session) = CreateMiddleware(spyStore, [
            new ChatResponseUpdate
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("hello")],
                CreatedAt = expected,
                FinishReason = ChatFinishReason.Stop,
            },
        ]);

        await ConsumeAsync(middleware, session);

        var message = Assert.Single(spyStore.StoredMessages);
        Assert.Equal(expected, message.CreatedAt);
    }

    [Fact]
    public async Task NoToolTurn_FinalAssistantMessage_IsRetainedInPersistedHistory()
    {
        // Issue #1103: End-to-end for a plain no-tool turn — the CopilotSdkStreamAdapter now
        // emits a terminal ChatResponseUpdate carrying FinishReason on SessionIdleEvent, so the
        // single assistant message of the turn is treated as stable and persisted. Previously
        // the final message was dropped from persisted history (Count == 1 → stableCount == 0).
        var channel = System.Threading.Channels.Channel.CreateUnbounded<GitHub.Copilot.SDK.SessionEvent>();
        channel.Writer.TryWrite(new GitHub.Copilot.SDK.AssistantMessageDeltaEvent
        {
            AgentId = string.Empty,
            Data = new GitHub.Copilot.SDK.AssistantMessageDeltaData
            {
                DeltaContent = "final reply",
                MessageId = "msg-1",
            },
        });
        channel.Writer.TryWrite(new GitHub.Copilot.SDK.SessionIdleEvent
        {
            Data = new GitHub.Copilot.SDK.SessionIdleData { Aborted = false },
        });
        channel.Writer.Complete();

        var adapterUpdates = new List<ChatResponseUpdate>();
        await foreach (var update in CopilotSdkStreamAdapter.TranslateCopilotSdkSessionEvents(channel.Reader, CancellationToken.None))
        {
            adapterUpdates.Add(update);
        }

        var spyStore = new SpyAgentPersistenceStore();
        var (middleware, session) = CreateMiddleware(spyStore, adapterUpdates.ToArray());

        await ConsumeAsync(middleware, session);

        var stored = Assert.Single(spyStore.StoredMessages);
        Assert.Equal(ChatRole.Assistant, stored.Role);
        Assert.Equal("final reply", GetText(stored));
    }

    private static (StreamingPersistenceMiddleware Middleware, AgentSession Session) CreateMiddleware(
        IAgentPersistenceStore store,
        ChatResponseUpdate[] updates)
    {
        var provider = CreateProvider(store);
        var inner = new StaticChatClient(updates);
        var middleware = new StreamingPersistenceMiddleware(inner, provider, store);
        var session = CreateSession(provider);
        middleware.SetCurrentSession(session);
        return (middleware, session);
    }

    private static IncrementalPersistenceChatHistoryProvider CreateProvider(IAgentPersistenceStore store)
    {
        var provider = new IncrementalPersistenceChatHistoryProvider(agentDefinition: null, store: store);
        provider.SetSessionSerializer((_, _) =>
            ValueTask.FromResult(BsonDocument.Parse("{}")));
        provider.SetAgentSessionId(null, AgentSessionId);
        return provider;
    }

    private static AgentSession CreateSession(IncrementalPersistenceChatHistoryProvider provider)
    {
        var session = new TestAgentSession();
        provider.SetAgentSessionId(session, AgentSessionId);
        return session;
    }

    private static ChatResponseUpdate MakeUpdate(string text, string? finishReason = null)
    {
        return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent(text)],
            FinishReason = finishReason is not null ? new ChatFinishReason(finishReason) : null,
        };
    }

    private static async Task<List<ChatResponseUpdate>> ConsumeAsync(
        StreamingPersistenceMiddleware middleware,
        AgentSession session)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in middleware.GetStreamingResponseAsync([], null, CancellationToken.None))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static string GetText(ChatMessage message)
        => string.Concat(message.Contents.OfType<TextContent>().Select(c => c.Text));

    private sealed class TestAgentSession : AgentSession
    {
    }

    private sealed class SpyAgentPersistenceStore : IAgentPersistenceStore
    {
        private readonly List<ChatMessage> storedMessages = [];
        private readonly List<PersistedAgent> storedAgents = [];

        public int StoreCallCount { get; private set; }

        public IReadOnlyList<ChatMessage> StoredMessages => this.storedMessages;

        public IReadOnlyList<PersistedAgent> StoredAgents => this.storedAgents;

        public ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
        {
            StoreCallCount++;
            this.storedAgents.Add(request.Agent);
            if (request.NewMessages is not null)
            {
                this.storedMessages.AddRange(request.NewMessages);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<PersistedAgent?> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<PersistedAgent?>(null);

        public ValueTask<ChatMessage[]> ReadMessagesAsync(ReadMessagesRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Array.Empty<ChatMessage>());

        public ValueTask AddSubAgentLinkAsync(string parentSessionId, string childSessionId, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<AgentSessionId>> ReadSubAgentChildIdsAsync(string parentSessionId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentSessionId>>(Array.Empty<AgentSessionId>());
    }

    /// <summary>A chat client that yields a fixed list of updates.</summary>
    private sealed class StaticChatClient : IChatClient
    {
        private readonly ChatResponseUpdate[] updates;

        public StaticChatClient(ChatResponseUpdate[] updates) => this.updates = updates;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in this.updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return update;
            }

            await Task.CompletedTask;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([]));

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    /// <summary>A chat client that blocks between update[0] and update[1] until a gate is opened.</summary>
    private sealed class GatingChatClient : IChatClient
    {
        private readonly TaskCompletionSource gate;
        private readonly ChatResponseUpdate[] updates;

        public GatingChatClient(TaskCompletionSource gate, ChatResponseUpdate[] updates)
        {
            this.gate = gate;
            this.updates = updates;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (this.updates.Length > 0)
            {
                yield return this.updates[0];
            }

            await this.gate.Task.WaitAsync(cancellationToken);

            for (var i = 1; i < this.updates.Length; i++)
            {
                yield return this.updates[i];
            }
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([]));

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }

    private sealed class ServiceExposingChatClient : IChatClient
    {
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([]));

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(ServiceExposingChatClient) ? this : null;

        public void Dispose() { }
    }

    private sealed class EmptyChatClient : IChatClient
    {
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([]));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
