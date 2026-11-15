using AgentSchema;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.Bson;
using Phantom.Workspaces.Llm.Interfaces;
using System.Collections.Concurrent;
using System.Text.Json;
using Phantom.Workspaces.Llm.Tests;

namespace Phantom.Workspaces.Llm.Core.Tests;

public class StreamingPersistenceMiddlewareTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AcceptedFlushBoundary_ObservesIndependentStoreAndProjectionOutcomesWithoutRetry(bool appendFails, bool projectionFails)
    {
        var appendFailure = new IOException("ambiguous boundary append");
        var projectionFailure = new InvalidOperationException("independent boundary projection");
        var store = new SpyAgentPersistenceStore { Failure = appendFails ? appendFailure : null };
        var provider = CreateProvider(store);
        using var sdk = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        CopilotSdkOutputDrain? owned = null;
        var inner = new FailingCleanupClient(null, null)
        {
            Copilot = sdk, ConfigureDrain = drain => owned = drain,
            BeforeTerminalRead = () => owned!.FlushAcceptedAsync(),
        };
        var projections = 0;
        var projectionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var projectionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new StreamingPersistenceMiddleware(inner, provider, store)
        {
            AcceptedProjectionBoundary = async () =>
            {
                projections++;
                projectionEntered.TrySetResult();
                await projectionRelease.Task;
                if (projectionFails) throw projectionFailure;
            },
        };
        var session = CreateSession(provider);
        middleware.SetCurrentSession(session);
        var consumption = ConsumeAsync(middleware, session);
        await projectionEntered.Task;
        var repeatedBoundary = owned!.FlushAcceptedAsync();
        try
        {
            Assert.False(consumption.IsCompleted);
            Assert.Equal(0, inner.Disposals);
            Assert.Equal(1, store.StoreCallCount);
        }
        finally { projectionRelease.TrySetResult(); }
        var failure = await Record.ExceptionAsync(() => consumption);
        Assert.Same(failure, await Record.ExceptionAsync(() => repeatedBoundary));
        if (!appendFails && !projectionFails) Assert.Null(failure);
        else
        {
            var errors = failure is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToArray() : [failure!];
            Assert.Equal((appendFails ? 1 : 0) + (projectionFails ? 1 : 0), errors.Length);
            if (appendFails) Assert.Contains(appendFailure, errors);
            if (projectionFails) Assert.Contains(projectionFailure, errors);
        }
        Assert.Equal(1, store.StoreCallCount);
        Assert.Equal(1, projections);
        Assert.Equal(1, inner.Disposals);
    }

    [Fact]
    public async Task AppendFailure_FencesNativeAdmissionBeforeProviderDisposalWithoutRetry()
    {
        var original = new IOException("ambiguous append original");
        var store = new SpyAgentPersistenceStore { Failure = original };
        var provider = CreateProvider(store);
        var root = new CopilotSdkRootEventQueue();
        var reader = root.Acquire(false);
        using var sdk = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        var inner = new FailingCleanupClient(null, null)
        {
            Copilot = sdk,
            ConfigureDrain = drain => drain.Attach(root.SealAccepted, root.Fail),
            BeforeDisposal = () =>
            {
                Assert.False(root.IsReceiving(reader));
                Assert.Same(original, Assert.Throws<IOException>(() =>
                {
                    _ = root.StartSteering(reader, () => true, () => throw new Xunit.Sdk.XunitException("must not start"));
                }));
            },
        };
        var middleware = new StreamingPersistenceMiddleware(inner, provider, store);
        var session = CreateSession(provider);
        middleware.SetCurrentSession(session);
        Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => ConsumeAsync(middleware, session)));
        Assert.Equal(1, store.StoreCallCount);
        Assert.Equal(1, inner.Disposals);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task IndependentProviderAppendAndDisposeFailures_RetainEveryOriginalIdentity(bool providerFails, bool appendFails, bool disposeFails)
    {
        var providerFailure = new IOException("provider original");
        var appendFailure = new InvalidOperationException("append original");
        var disposeFailure = new ApplicationException("dispose original");
        var store = new SpyAgentPersistenceStore { Failure = appendFails ? appendFailure : null };
        var provider = CreateProvider(store);
        var inner = new FailingCleanupClient(providerFails ? providerFailure : null, disposeFails ? disposeFailure : null);
        var middleware = new StreamingPersistenceMiddleware(inner, provider, store);
        var session = CreateSession(provider);
        middleware.SetCurrentSession(session);
        var failure = await Assert.ThrowsAsync<AggregateException>(() => ConsumeAsync(middleware, session));
        var expected = new Exception?[] { providerFails ? providerFailure : null, appendFailure, disposeFails ? disposeFailure : null }.Where(value => value is not null).ToArray();
        Assert.Equal(expected.Length, failure.InnerExceptions.Count);
        for (var index = 0; index < expected.Length; index++) Assert.Same(expected[index], failure.InnerExceptions[index]);
        Assert.Equal(1, store.StoreCallCount);
        Assert.Equal(1, inner.Disposals);
    }

    private sealed class FailingCleanupClient(Exception? providerFailure, Exception? disposalFailure) : IChatClient, IAsyncEnumerable<ChatResponseUpdate>, IAsyncEnumerator<ChatResponseUpdate>
    {
        public CopilotSdkChatClient? Copilot { get; init; }
        public Action<CopilotSdkOutputDrain>? ConfigureDrain { get; init; }
        public Action? BeforeDisposal { get; init; }
        public Func<Task>? BeforeTerminalRead { get; init; }
        public int Disposals { get; private set; }
        private int reads;
        public ChatResponseUpdate Current => MakeUpdate("accepted trailing output");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (options?.AdditionalProperties?.GetValueOrDefault(CopilotSdkOutputDrain.OptionName) is CopilotSdkOutputDrain drain)
                this.ConfigureDrain?.Invoke(drain);
            return this;
        }
        public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public async ValueTask<bool> MoveNextAsync()
        {
            if (this.reads++ == 0) return true;
            if (this.BeforeTerminalRead is { } beforeRead) await beforeRead();
            if (providerFailure is not null) throw providerFailure;
            return false;
        }
        public ValueTask DisposeAsync()
        {
            this.Disposals++;
            this.BeforeDisposal?.Invoke();
            return disposalFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(disposalFailure);
        }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(CopilotSdkChatClient) ? this.Copilot : null;
        public void Dispose() { }
    }

    [Fact]
    public async Task DocumentOrigin_MessagePersistenceAndJsonReload_PreserveGenerationDirectory()
    {
        var store = new SpyAgentPersistenceStore();
        var (middleware, session) = CreateMiddleware(store, [MakeUpdate("report.md", finishReason: "stop")]);
        var origin = new AgentDocumentOrigin(@"C:\original\directory", true);
        middleware.SetAssistantRunId("original-run", origin);
        await ConsumeAsync(middleware, session);
        var serialized = JsonSerializer.Serialize(Assert.Single(store.StoredMessages), AIJsonUtilities.DefaultOptions);
        var reloaded = JsonSerializer.Deserialize<ChatMessage>(serialized, AIJsonUtilities.DefaultOptions)!;
        Assert.Equal(origin, AgentDocumentOrigin.Read(reloaded));
        Assert.Null(AgentDocumentOrigin.Read(new ChatMessage(ChatRole.Assistant, "legacy report.md")));
        var persisted = new InMemoryAgentPersistenceStore();
        await persisted.StoreAsync(new StoreRequestAgent
        {
            Agent = new PersistedAgent
            {
                AgentSessionId = "origin-restart",
                AgentDefinitionJson = BsonDocument.Parse("""
                    {"kind":"prompt","name":"origin","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
                    """),
            },
            NewMessages = [reloaded],
        });
        await using var factory = new AgentChatFactory(persisted, new AgentServices(), TaskScheduler.Default);
        await using var lease = await factory.GetAsync(new Phantom.Workspaces.Llm.Interfaces.AgentSessionId("origin-restart"));
        var restoredItem = Assert.Single(lease.AgentChat.History, item => item.Role == ChatRole.Assistant);
        Assert.Equal(origin, restoredItem.DocumentOrigin);
        Assert.Equal("original-run", restoredItem.AssistantRunId);
    }

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
    public async Task AcceptedOutput_ConsumerDisposesEarly_IsDurable()
    {
        var store = new SpyAgentPersistenceStore();
        var (middleware, _) = CreateMiddleware(store, [MakeUpdate("accepted partial")]);
        await using (var output = middleware.GetStreamingResponseAsync([]).GetAsyncEnumerator())
        {
            Assert.True(await output.MoveNextAsync());
            Assert.Empty(store.StoredMessages);
        }
        Assert.Equal("accepted partial", GetText(Assert.Single(store.StoredMessages)));
    }

    [Fact]
    public async Task PersistenceFailure_IsNotRetriedDuringCleanup()
    {
        var store = new SpyAgentPersistenceStore { Failure = new IOException("synthetic append failure") };
        var (middleware, session) = CreateMiddleware(store, [MakeUpdate("accepted", finishReason: "stop")]);
        await Assert.ThrowsAsync<IOException>(() => ConsumeAsync(middleware, session));
        Assert.Equal(1, store.StoreCallCount);
    }

    [Fact]
    public async Task AcceptedOutput_LateCleanupRetainsOriginalSessionRunAndOrigin()
    {
        var store = new SpyAgentPersistenceStore();
        var provider = CreateProvider(store);
        var originalSession = CreateSession(provider);
        provider.SetCopilotSdkSessionId("original-native");
        var nextSession = new TestAgentSession();
        provider.SetAgentSessionId(nextSession, "new-product-session");
        var originalOrigin = new AgentDocumentOrigin(@"C:\original", true);
        var middleware = new StreamingPersistenceMiddleware(new StaticChatClient([MakeUpdate("accepted")]), provider, store);
        middleware.SetCurrentSession(originalSession);
        middleware.SetAssistantRunId("original-run", originalOrigin);
        await using (var output = middleware.GetStreamingResponseAsync([]).GetAsyncEnumerator())
        {
            Assert.True(await output.MoveNextAsync());
            middleware.SetCurrentSession(nextSession);
            provider.SetCopilotSdkSessionId("next-native");
            middleware.SetAssistantRunId("next-run", new AgentDocumentOrigin(@"C:\next", true));
        }
        Assert.Equal(AgentSessionId, Assert.Single(store.StoredAgents).AgentSessionId);
        Assert.Equal("original-native", store.StoredAgents[0].CopilotSdkSessionId);
        var message = Assert.Single(store.StoredMessages);
        Assert.Equal("original-run", message.AdditionalProperties![StreamingPersistenceMiddleware.AssistantRunIdProperty]);
        Assert.Equal(originalOrigin, AgentDocumentOrigin.Read(message));
    }

    [Fact]
    public async Task CommentaryTaskResultAndFinal_PreserveEveryAcceptedContent()
    {
        var store = new SpyAgentPersistenceStore();
        var (middleware, session) = CreateMiddleware(store,
        [
            MakeUpdate("commentary"),
            new() { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("task", "task")] },
            new() { Role = ChatRole.Tool, Contents = [new FunctionResultContent("task", "completed")] },
            MakeUpdate("final", "stop"),
        ]);
        await ConsumeAsync(middleware, session);
        Assert.Equal(["commentary", "final"], store.StoredMessages.SelectMany(message => message.Contents).OfType<TextContent>().Select(content => content.Text));
        Assert.Single(store.StoredMessages.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Single(store.StoredMessages.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
    }

    [Fact]
    public async Task ProviderFailure_AfterAcceptedTrailingResult_FlushesWithoutMaskingFailure()
    {
        var store = new SpyAgentPersistenceStore();
        var provider = CreateProvider(store);
        var failure = new IOException("synthetic provider failure");
        var inner = new StaticChatClient(
        [
            new() { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("call", "task")] },
            new() { Role = ChatRole.Tool, Contents = [new FunctionResultContent("call", "completed")] },
        ]) { EndFailure = failure };
        var middleware = new StreamingPersistenceMiddleware(inner, provider, store);
        var session = CreateSession(provider);
        middleware.SetCurrentSession(session);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => ConsumeAsync(middleware, session)));
        Assert.Equal(2, store.StoredMessages.Count);
        Assert.Equal("call", Assert.Single(store.StoredMessages[1].Contents.OfType<FunctionResultContent>()).CallId);
        Assert.Equal(2, store.StoreCallCount);
    }

    [Fact]
    public async Task ConsecutiveTerminalBatches_DoNotMergeNewTaskIntoAnAlreadyPersistedFinal()
    {
        var store = new SpyAgentPersistenceStore();
        var (middleware, session) = CreateMiddleware(store,
        [
            MakeUpdate("continued final", "stop"),
            new() { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("next", "task")] },
            new() { Role = ChatRole.Tool, Contents = [new FunctionResultContent("next", "completed")] },
            MakeUpdate("next final", "stop"),
        ]);
        await ConsumeAsync(middleware, session);
        Assert.Equal(4, store.StoredMessages.Count);
        Assert.Equal("continued final", store.StoredMessages[0].Text);
        Assert.Equal("next", Assert.Single(store.StoredMessages[1].Contents.OfType<FunctionCallContent>()).CallId);
        Assert.Equal("next", Assert.Single(store.StoredMessages[2].Contents.OfType<FunctionResultContent>()).CallId);
        Assert.Equal("next final", store.StoredMessages[3].Text);
    }

    [Fact]
    public async Task SeparateServiceCallsWithinOneAssistantRun_PersistSameBoundary()
    {
        var store = new SpyAgentPersistenceStore();
        var (middleware, session) = CreateMiddleware(store, [MakeUpdate("one", finishReason: "stop")]);
        middleware.SetAssistantRunId("run-1");
        await ConsumeAsync(middleware, session);
        await ConsumeAsync(middleware, session);
        middleware.SetAssistantRunId("run-2");
        await ConsumeAsync(middleware, session);
        Assert.Equal(["run-1", "run-1", "run-2"], store.StoredMessages.Select(message =>
            message.AdditionalProperties?[StreamingPersistenceMiddleware.AssistantRunIdProperty]?.ToString() ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task NormalStreamEndWithoutFinishReason_PersistsTrailingToolResult()
    {
        var store = new SpyAgentPersistenceStore();
        var (middleware, session) = CreateMiddleware(store,
        [
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("call", "read")] },
            new ChatResponseUpdate { Role = ChatRole.Tool, Contents = [new FunctionResultContent("call", "value")] },
        ]);
        await ConsumeAsync(middleware, session);
        Assert.Equal(2, store.StoredMessages.Count);
        Assert.Equal(store.StoredMessages[0].AdditionalProperties?[StreamingPersistenceMiddleware.AssistantRunIdProperty]?.ToString(),
            store.StoredMessages[1].AdditionalProperties?[StreamingPersistenceMiddleware.AssistantRunIdProperty]?.ToString());
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
    public async Task StreamingPersistenceMiddleware_LastMessageStabilizedByTerminalFinishReason_IsPersistedBeforeStreamCompletes()
    {
        var spyStore = new SpyAgentPersistenceStore();
        var terminalGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalPullStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new TerminalGatingChatClient(
            terminalGate,
            terminalPullStarted,
            [
                new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new FunctionCallContent("call-shell-1", "shell", null)],
                },
                new ChatResponseUpdate
                {
                    Role = ChatRole.Tool,
                    Contents = [new FunctionResultContent("call-shell-1", "shell-ok")],
                },
                new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    FinishReason = ChatFinishReason.Stop,
                },
            ]);
        var provider = CreateProvider(spyStore);
        var middleware = new StreamingPersistenceMiddleware(client, provider, spyStore);
        var session = CreateSession(provider);
        middleware.SetCurrentSession(session);
        var enumerator = middleware.GetStreamingResponseAsync([], null, CancellationToken.None)
            .GetAsyncEnumerator(CancellationToken.None);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Single(spyStore.StoredMessages);
        Assert.DoesNotContain(
            spyStore.StoredMessages,
            message => message.Contents.OfType<FunctionResultContent>()
                .Any(result => result.CallId == "call-shell-1"));

        var terminalMove = enumerator.MoveNextAsync().AsTask();
        await terminalPullStarted.Task;
        Assert.False(terminalMove.IsCompleted);

        terminalGate.SetResult();
        Assert.True(await terminalMove);
        Assert.Contains(
            spyStore.StoredMessages,
            message => message.Contents.OfType<FunctionResultContent>()
                .Any(result => result.CallId == "call-shell-1"));

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
        var runIds = spyStore.StoredMessages.Select(message =>
            message.AdditionalProperties?[StreamingPersistenceMiddleware.AssistantRunIdProperty]?.ToString()).ToArray();
        Assert.All(runIds, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Single(runIds.Distinct());
        var restored = spyStore.StoredMessages.Select(message =>
            JsonSerializer.Deserialize<ChatMessage>(
                JsonSerializer.Serialize(message, AIJsonUtilities.DefaultOptions),
                AIJsonUtilities.DefaultOptions)!).ToArray();
        Assert.All(restored, message => Assert.Equal(runIds[0],
            message.AdditionalProperties?[StreamingPersistenceMiddleware.AssistantRunIdProperty]?.ToString()));
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
        Assert.Equal(2, spyStore.StoredMessages.Count);
        Assert.Equal(ChatRole.Assistant, spyStore.StoredMessages[0].Role);
        Assert.Contains(spyStore.StoredMessages[0].Contents, c => c is FunctionCallContent fc && fc.CallId == "c0");
        Assert.Contains(spyStore.StoredMessages[1].Contents, c => c is FunctionResultContent result && result.CallId == "c0");
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
        var channel = System.Threading.Channels.Channel.CreateUnbounded<GitHub.Copilot.SessionEvent>();
        channel.Writer.TryWrite(new GitHub.Copilot.AssistantMessageDeltaEvent
        {
            AgentId = string.Empty,
            Data = new GitHub.Copilot.AssistantMessageDeltaData
            {
                DeltaContent = "final reply",
                MessageId = "msg-1",
            },
        });
        channel.Writer.TryWrite(new GitHub.Copilot.SessionIdleEvent
        {
            Data = new GitHub.Copilot.SessionIdleData { Aborted = false },
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

    [Fact]
    public async Task StreamingPersistenceMiddleware_CoalescingRunsOffCapturedSynchronizationContext()
    {
        // #1327: when consumed from a thread carrying a (UI-like) SynchronizationContext, the
        // inner-stream enumeration + ToChatResponse coalescing must run off that context (on the
        // thread pool), never on the captured context.
        using var pump = new AgentChatForegroundContextTests.SingleThreadPump(installSynchronizationContext: true);
        var innerContexts = new ConcurrentBag<SynchronizationContext?>();
        var spyStore = new SpyAgentPersistenceStore();
        var innerClient = new ContextRecordingChatClient(RoleAlternatingUpdates(), innerContexts, storeContexts: null);

        var provider = CreateProvider(spyStore);
        var middleware = new StreamingPersistenceMiddleware(innerClient, provider, spyStore);
        var session = CreateSession(provider);
        middleware.SetCurrentSession(session);

        await pump.PostAsync(async () =>
        {
            Assert.Same(pump.Context, SynchronizationContext.Current);
            await foreach (var _ in middleware.GetStreamingResponseAsync([], null, CancellationToken.None))
            {
            }

            return true;
        });

        Assert.NotEmpty(innerContexts);
        Assert.All(innerContexts, ctx => Assert.NotSame(pump.Context, ctx));
    }

    [Fact]
    public async Task StreamingPersistenceMiddleware_PersistMessageAsyncRunsOffCapturedSynchronizationContext()
    {
        // #1327: the persistence-store write must also run off the captured (UI-like) context.
        using var pump = new AgentChatForegroundContextTests.SingleThreadPump(installSynchronizationContext: true);
        var storeContexts = new ConcurrentBag<SynchronizationContext?>();
        var spyStore = new ContextRecordingStore(storeContexts);
        var innerClient = new ContextRecordingChatClient(RoleAlternatingUpdates(), recordEnumeration: null, storeContexts: null);

        var provider = CreateProvider(spyStore);
        var middleware = new StreamingPersistenceMiddleware(innerClient, provider, spyStore);
        var session = CreateSession(provider);
        middleware.SetCurrentSession(session);

        await pump.PostAsync(async () =>
        {
            Assert.Same(pump.Context, SynchronizationContext.Current);
            await foreach (var _ in middleware.GetStreamingResponseAsync([], null, CancellationToken.None))
            {
            }

            return true;
        });

        Assert.NotEmpty(storeContexts);
        Assert.All(storeContexts, ctx => Assert.NotSame(pump.Context, ctx));
    }

    [Fact]
    public async Task StreamingPersistenceMiddleware_YieldOrdering_StableMessagePersistedBeforeNextUpdateYielded()
    {
        // #1327: after offloading, the ordering invariant must still hold — when message N
        // becomes stable, StoreAsync(N) completes before update N+1 is returned to the consumer.
        var spyStore = new SpyAgentPersistenceStore();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockingClient = new GatingChatClient(gate, [
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("c1", "tool", null)] },
            new ChatResponseUpdate { Role = ChatRole.Tool, Contents = [new FunctionResultContent("c1", "result")] },
        ]);

        var provider = CreateProvider(spyStore);
        var middleware = new StreamingPersistenceMiddleware(blockingClient, provider, spyStore);
        var frameworkSession = CreateSession(provider);
        middleware.SetCurrentSession(frameworkSession);

        var enumerator = middleware.GetStreamingResponseAsync([], null, CancellationToken.None)
            .GetAsyncEnumerator(CancellationToken.None);

        await enumerator.MoveNextAsync();
        Assert.Empty(spyStore.StoredMessages);

        gate.SetResult();
        await enumerator.MoveNextAsync();

        // The stable message[0] is durable before update[1] is observed by the consumer.
        Assert.Single(spyStore.StoredMessages);
        Assert.Equal(ChatRole.Assistant, spyStore.StoredMessages[0].Role);

        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task StreamingPersistenceMiddleware_CancellationTokenPropagates_ToInnerEnumerationAndPump()
    {
        // #1327: when the caller cancels, the offloaded work observes cancellation on the inner
        // enumeration and the consumer's MoveNextAsync surfaces an OperationCanceledException.
        var spyStore = new SpyAgentPersistenceStore();
        var innerClient = new CancellationObservingChatClient(
            new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("first")] });

        var provider = CreateProvider(spyStore);
        var middleware = new StreamingPersistenceMiddleware(innerClient, provider, spyStore);
        var session = CreateSession(provider);
        middleware.SetCurrentSession(session);

        using var cts = new CancellationTokenSource();
        var enumerator = middleware.GetStreamingResponseAsync([], null, cts.Token)
            .GetAsyncEnumerator(cts.Token);

        Assert.True(await enumerator.MoveNextAsync());

        var pending = enumerator.MoveNextAsync();
        await innerClient.EnumerationBlocked;
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        Assert.True(innerClient.InnerObservedCancellation);

        await enumerator.DisposeAsync();
    }

    private static ChatResponseUpdate[] RoleAlternatingUpdates() =>
    [
        new() { Role = ChatRole.Assistant, Contents = [new FunctionCallContent("c0", "tool", null)] },
        new() { Role = ChatRole.Tool,      Contents = [new FunctionResultContent("c0", "r0")] },
        new() { Role = ChatRole.Assistant, Contents = [new TextContent("final")], FinishReason = ChatFinishReason.Stop },
    ];

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
        public Exception? Failure { get; init; }

        public IReadOnlyList<ChatMessage> StoredMessages => this.storedMessages;

        public IReadOnlyList<PersistedAgent> StoredAgents => this.storedAgents;

        public ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
        {
            StoreCallCount++;
            if (this.Failure is { } failure) throw failure;
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
        public Exception? EndFailure { get; init; }

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
            if (this.EndFailure is { } failure) throw failure;
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

    private sealed class TerminalGatingChatClient : IChatClient
    {
        private readonly TaskCompletionSource terminalGate;
        private readonly TaskCompletionSource terminalPullStarted;
        private readonly ChatResponseUpdate[] updates;

        public TerminalGatingChatClient(
            TaskCompletionSource terminalGate,
            TaskCompletionSource terminalPullStarted,
            ChatResponseUpdate[] updates)
        {
            this.terminalGate = terminalGate;
            this.terminalPullStarted = terminalPullStarted;
            this.updates = updates;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return this.updates[0];
            yield return this.updates[1];

            this.terminalPullStarted.SetResult();
            await this.terminalGate.Task.WaitAsync(cancellationToken);
            yield return this.updates[2];
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
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

    /// <summary>
    /// A chat client that records <see cref="SynchronizationContext.Current"/> at the point each
    /// update is produced (i.e. where the middleware's inner enumeration + coalescing runs).
    /// </summary>
    private sealed class ContextRecordingChatClient : IChatClient
    {
        private readonly ChatResponseUpdate[] updates;
        private readonly ConcurrentBag<SynchronizationContext?>? recordEnumeration;

        public ContextRecordingChatClient(
            ChatResponseUpdate[] updates,
            ConcurrentBag<SynchronizationContext?>? recordEnumeration,
            ConcurrentBag<SynchronizationContext?>? storeContexts)
        {
            this.updates = updates;
            this.recordEnumeration = recordEnumeration;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in this.updates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.recordEnumeration?.Add(SynchronizationContext.Current);
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

    /// <summary>
    /// A persistence store that records <see cref="SynchronizationContext.Current"/> at each write.
    /// </summary>
    private sealed class ContextRecordingStore : IAgentPersistenceStore
    {
        private readonly ConcurrentBag<SynchronizationContext?> contexts;

        public ContextRecordingStore(ConcurrentBag<SynchronizationContext?> contexts)
            => this.contexts = contexts;

        public ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
        {
            this.contexts.Add(SynchronizationContext.Current);
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

    /// <summary>
    /// Yields one update, then blocks (event-driven) until its enumeration token is cancelled,
    /// recording that the inner enumeration observed the cancellation.
    /// </summary>
    private sealed class CancellationObservingChatClient : IChatClient
    {
        private readonly ChatResponseUpdate first;
        private readonly TaskCompletionSource enumerationBlocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationObservingChatClient(ChatResponseUpdate first) => this.first = first;

        public bool InnerObservedCancellation { get; private set; }

        public Task EnumerationBlocked => this.enumerationBlocked.Task;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return this.first;

            var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (cancellationToken.Register(() => wait.TrySetResult()))
            {
                this.enumerationBlocked.TrySetResult();
                await wait.Task.ConfigureAwait(false);
            }

            this.InnerObservedCancellation = cancellationToken.IsCancellationRequested;
            cancellationToken.ThrowIfCancellationRequested();
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse([]));

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(IChatClient) ? this : null;

        public void Dispose() { }
    }
}
