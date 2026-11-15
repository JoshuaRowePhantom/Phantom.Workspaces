using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Core.Tests.Infrastructure;
using Phantom.Workspaces.Llm.Interfaces;
using MongoDB.Bson;
using System.Text.Json;
using System.Collections.Specialized;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class CopilotSdkRootContinuationTests
{
    [Fact]
    public Task AgentChat_RetirementPending_PersistsSubsequentTasksAndContinuationBeforeRelease()
    {
        var store = new InMemoryAgentPersistenceStore();
        return CopilotSdkRetirementPersistenceScenario.RunAsync(() => store);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PersistenceBlocked_AcceptedSdkSuffixSurvivesCancellationOrEarlyDisposal(bool cancel, bool gateRouting)
    {
        var session = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(session)));
        var routingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRouting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var table = new SubAgentTestFakes.FakeSubAgentTable();
        client.SetSubAgentDependencies(new SubAgentTestFakes.FakeRunningAgentChatFactory
        {
            BeforeCreateAsync = async () => { routingEntered.TrySetResult(); await releaseRouting.Task; },
        }, table);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new SerializedStore
        {
            BeforeStoreAsync = async () => { entered.TrySetResult(); await release.Task; },
        };
        var provider = new IncrementalPersistenceChatHistoryProvider(AgentDefinitionLoader.LoadAgentFromJson(Definition), store);
        provider.SetSessionSerializer((_, _) => ValueTask.FromResult(new BsonDocument()));
        var frameworkSession = new DrainTestSession();
        provider.SetAgentSessionId(frameworkSession, "owned-product");
        client.SessionEstablished += provider.SetCopilotSdkSessionId;
        var middleware = new StreamingPersistenceMiddleware(client, provider, store);
        middleware.SetCurrentSession(frameworkSession);
        middleware.SetAssistantRunId("owned-run");
        using var cancellation = new CancellationTokenSource();
        var output = middleware.GetStreamingResponseAsync([new(ChatRole.User, "one request")], cancellationToken: cancellation.Token)
            .GetAsyncEnumerator();
        session.EnqueueEvent(new AssistantMessageDeltaEvent { Data = new() { MessageId = "prefix", DeltaContent = "accepted prefix" } });
        session.EnqueueEvent(new ToolExecutionStartEvent { Data = new() { ToolCallId = "first", ToolName = "read" } });
        session.EnqueueEvent(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "first", Success = true, Result = new() { Content = "first result" } } });
        var blocked = ReadUntilPersistenceAsync();
        await entered.Task;
        var suffix = TaskFinal("suffix", "accepted suffix final");
        session.Emit(suffix[0]);
        if (gateRouting)
        {
            session.Emit(new SubagentStartedEvent
            {
                Id = Guid.NewGuid(), AgentId = "child",
                Data = new() { ToolCallId = "suffix", AgentName = "task", AgentDisplayName = "Task", AgentDescription = "synthetic gate" },
            });
            await routingEntered.Task;
        }
        foreach (var value in suffix.Skip(1)) session.Emit(value);
        foreach (var value in TaskFinal("pending", "accepted pending final")) session.Emit(value);
        if (cancel) cancellation.Cancel();
        releaseRouting.TrySetResult();
        release.TrySetResult();
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await blocked);
        else
            await blocked;
        await output.DisposeAsync();
        var durable = await store.ReadMessagesAsync(new() { AgentSessionId = "owned-product" });
        Assert.Equal(["first", "suffix", "pending"], durable.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Select(call => call.CallId));
        Assert.Equal(["first", "suffix", "pending"], durable.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Select(result => result.CallId));
        Assert.Equal(["accepted prefix", "accepted suffix final", "accepted pending final"], durable.Where(message => message.Contents.OfType<TextContent>().Any()).Select(message => message.Text));
        Assert.Equal(session.SessionId, (await store.RestoreAsync(new() { AgentSessionId = "owned-product" }))!.Value.CopilotSdkSessionId);
        Assert.All(durable, message => Assert.Equal("owned-run", message.AdditionalProperties![StreamingPersistenceMiddleware.AssistantRunIdProperty]?.ToString()));
        Assert.Equal(1, session.SendAsyncCallCount);
        Assert.False(client.HasPendingRootContinuation);
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        await using var restored = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = "owned-product",
            AgentDefinition = null,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            ForegroundScheduler = scheduler.ExclusiveScheduler,
        });
        Assert.Equal(3, restored.History.SelectMany(item => item.Contents).OfType<FunctionCallContent>().Count());
        Assert.Equal(3, restored.History.SelectMany(item => item.Contents).OfType<FunctionResultContent>().Count());
        Assert.Contains(restored.History, item => item.Contents.OfType<TextContent>().Any(content => content.Text == "accepted pending final"));
        foreach (var child in table.AddedChats) await child.DisposeAsync();

        async Task ReadUntilPersistenceAsync()
        {
            while (await output.MoveNextAsync())
                if (entered.Task.IsCompleted) return;
        }
    }

    private sealed class DrainTestSession : Microsoft.Agents.AI.AgentSession;

    [Fact]
    public async Task NonStreamingResponse_ConsumesItsOwnRootEventsWithoutDuplicateContinuation()
    {
        var final = new AssistantMessageEvent { Data = new() { MessageId = "answer", Content = "one final", ReasoningText = "reason" } };
        var session = new FakeCopilotSession { SendAndWaitResult = final };
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(session)));
        session.EnqueueEvent(final);
        session.EnqueueEvent(new SessionIdleEvent { Data = new() });
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "one request")]);
        Assert.Equal("one final", response.Text);
        Assert.Equal("answer", response.ResponseId);
        Assert.Equal("reason", Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<TextReasoningContent>()).Text);
        Assert.False(client.HasPendingRootContinuation);
        Assert.Equal(1, session.SendAsyncCallCount);
    }

    private const string Definition = """
        {"kind":"prompt","name":"durable-root","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
        """;

    [Fact]
    public async Task AgentChat_TaskFinalContinuationAndNextPrompt_SurviveFreshRestoreUnderProductIdentity()
    {
        var session = new FakeCopilotSession { SessionId = "native-provider-identity" };
        var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(session)));
        var store = new SerializedStore();
        await store.StoreAsync(new()
        {
            Agent = new()
            {
                AgentSessionId = "stable-product-identity",
                AgentDefinitionJson = BsonDocument.Parse(Definition),
            },
            NewMessages = [new(ChatRole.Assistant, "older than native journal")],
        });
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        await using (var chat = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = "stable-product-identity",
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(Definition),
            ConfiguredStore = store,
            ClientOverride = client,
            ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new() { RunningAgentChatFactory = new SubAgentTestFakes.FakeRunningAgentChatFactory() },
        }))
        {
            EnqueueTaskFinal(session, "first", "first final");
            var firstSettled = WhenSettled(chat, "first final");
            chat.EnqueueUserMessage("first request");
            await firstSettled;
            var continuationSettled = WhenSettled(chat, "notification final");
            EmitTaskFinal(session, "notification", "notification final");
            await continuationSettled;
            Assert.Equal(1, session.SendAsyncCallCount);
            EnqueueTaskFinal(session, "next", "next final");
            var nextSettled = WhenSettled(chat, "next final");
            chat.EnqueueUserMessage("next request");
            await nextSettled;
            Assert.Equal(2, session.SendAsyncCallCount);
            Assert.Equal(3, chat.History.SelectMany(item => item.Contents).OfType<FunctionCallContent>().Count());
            Assert.Equal(3, chat.History.SelectMany(item => item.Contents).OfType<FunctionResultContent>().Count());
        }
        await using var restored = await AgentChat.CreateAsync(new()
        {
            AgentDefinition = null,
            AgentSessionId = "stable-product-identity",
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            ForegroundScheduler = scheduler.ExclusiveScheduler,
        });
        var durable = await store.ReadMessagesAsync(new() { AgentSessionId = "stable-product-identity" });
        Assert.Equal(12, durable.Length);
        Assert.Equal(["older than native journal", "first request", "first final", "notification final", "next request", "next final"],
            durable.Where(message => message.Contents.OfType<TextContent>().Any()).Select(message => message.Text).ToArray());
        Assert.Equal(3, restored.History.SelectMany(item => item.Contents).OfType<FunctionCallContent>().Count());
        Assert.Equal(3, restored.History.SelectMany(item => item.Contents).OfType<FunctionResultContent>().Count());
        Assert.Contains(restored.History, item => item.Contents.OfType<TextContent>().Any(content => content.Text == "older than native journal"));
        Assert.Equal("native-provider-identity",
            (await store.RestoreAsync(new() { AgentSessionId = "stable-product-identity" }))!.Value.CopilotSdkSessionId);
        Assert.Empty(await store.ReadMessagesAsync(new() { AgentSessionId = "native-provider-identity" }));
    }

    private static Task WhenSettled(AgentChat chat, string text)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (chat.RunningItems.Count == 0
                && chat.History.Any(item => item.Contents.OfType<TextContent>().Any(content => content.Text == text)))
            {
                ((INotifyCollectionChanged)chat.History).CollectionChanged -= Changed;
                ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged -= Changed;
                completion.TrySetResult();
            }
        }
        ((INotifyCollectionChanged)chat.History).CollectionChanged += Changed;
        ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged += Changed;
        return completion.Task;
    }

    [Fact]
    public async Task FailedRestoreAndOlderMetadataSnapshot_DoNotReplaceDurableTranscript()
    {
        var store = new SerializedStore();
        var agent = new PersistedAgent
        {
            AgentSessionId = "restore-product",
            CopilotSdkSessionId = "new-native-era",
            AgentDefinitionJson = BsonDocument.Parse(Definition),
        };
        await store.StoreAsync(new()
        {
            Agent = agent,
            NewMessages = [new(ChatRole.Assistant, "older durable answer")],
        });
        var failure = new IOException("synthetic restore failure");
        store.RestoreFailure = failure;
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => AgentChat.CreateAsync(new()
        {
            AgentSessionId = "restore-product",
            AgentDefinition = null,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
        })));
        store.RestoreFailure = null;
        await store.StoreAsync(new() { Agent = agent with { AgentSessionJson = new() }, NewMessages = [] });
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        await using var restored = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = "restore-product",
            AgentDefinition = null,
            ConfiguredStore = store,
            ClientOverride = new DeterministicTestChatClient(),
            ForegroundScheduler = scheduler.ExclusiveScheduler,
        });
        Assert.Equal("older durable answer",
            Assert.Single((await store.ReadMessagesAsync(new() { AgentSessionId = "restore-product" }))).Text);
        Assert.Contains(restored.History, item => item.Contents.OfType<TextContent>().Any(content => content.Text == "older durable answer"));
    }

    private static SessionEvent[] TaskFinal(string call, string text) =>
    [
        new ToolExecutionStartEvent { Data = new() { ToolCallId = call, ToolName = "task" } },
        new ToolExecutionCompleteEvent { Data = new() { ToolCallId = call, Success = true, Result = new() { Content = "synthetic result" } } },
        new AssistantMessageEvent { Data = new() { MessageId = call + "-answer", Content = text } },
        new SessionIdleEvent { Data = new() },
    ];

    private static void EnqueueTaskFinal(FakeCopilotSession session, string call, string text)
    {
        foreach (var value in TaskFinal(call, text)) session.EnqueueEvent(value);
    }

    private static void EmitTaskFinal(FakeCopilotSession session, string call, string text)
    {
        foreach (var value in TaskFinal(call, text)) session.Emit(value);
    }

    [Fact]
    public async Task PendingContinuationAtNewRequest_DrainsBeforeSendingAndConsumingTheNewRequest()
    {
        var session = new FakeCopilotSession();
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(session)));
        client.SetSubAgentDependencies(new SubAgentTestFakes.FakeRunningAgentChatFactory(), new SubAgentTestFakes.FakeSubAgentTable());
        session.EnqueueEvent(new SessionIdleEvent { Data = new() });
        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "initial")])) { }
        EmitTaskFinal(session, "continued", "continued final");
        EnqueueTaskFinal(session, "next", "next final");
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "next")]))
            updates.Add(update);
        Assert.Equal(["continued", "next"], updates.SelectMany(update => update.Contents).OfType<FunctionCallContent>().Select(call => call.CallId));
        Assert.Equal(["continued", "next"], updates.SelectMany(update => update.Contents).OfType<FunctionResultContent>().Select(result => result.CallId));
        Assert.Equal(["continued final", "next final"], updates.SelectMany(update => update.Contents).OfType<TextContent>().Select(text => text.Text));
        Assert.Equal(2, session.SendAsyncCallCount);
        Assert.False(client.HasPendingRootContinuation);
    }

    private sealed class SerializedStore : IAgentPersistenceStore
    {
        private readonly InMemoryAgentPersistenceStore inner = new();
        public Exception? RestoreFailure { get; set; }
        public Func<Task>? BeforeStoreAsync { get; init; }
        public async ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
        {
            if (this.BeforeStoreAsync is { } before) await before();
            await this.inner.StoreAsync(request with
            {
                NewMessages = request.NewMessages?.Select(Clone).ToArray(),
                Agent = request.Agent with
                {
                    AgentDefinitionJson = request.Agent.AgentDefinitionJson?.DeepClone().AsBsonDocument,
                    AgentSessionJson = request.Agent.AgentSessionJson?.DeepClone().AsBsonDocument,
                },
            }, cancellationToken);
        }
        public ValueTask<PersistedAgent?> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default) =>
            this.RestoreFailure is { } failure ? ValueTask.FromException<PersistedAgent?>(failure)
                : this.inner.RestoreAsync(request, cancellationToken);
        public async ValueTask<ChatMessage[]> ReadMessagesAsync(ReadMessagesRequest request, CancellationToken cancellationToken = default) =>
            (await this.inner.ReadMessagesAsync(request, cancellationToken)).Select(Clone).ToArray();
        public ValueTask AddSubAgentLinkAsync(string parentSessionId, string childSessionId, CancellationToken cancellationToken = default) =>
            this.inner.AddSubAgentLinkAsync(parentSessionId, childSessionId, cancellationToken);
        public ValueTask<IReadOnlyList<AgentSessionId>> ReadSubAgentChildIdsAsync(string parentSessionId, CancellationToken cancellationToken = default) =>
            this.inner.ReadSubAgentChildIdsAsync(parentSessionId, cancellationToken);
        private static ChatMessage Clone(ChatMessage message) =>
            JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(message, AIJsonUtilities.DefaultOptions), AIJsonUtilities.DefaultOptions)!;
    }

    [Fact]
    public async Task RootOutputAfterIdle_IsConsumedWithoutResendingPromptOrTools()
    {
        var session = new FakeCopilotSession();
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(session)));
        client.SetSubAgentDependencies(new SubAgentTestFakes.FakeRunningAgentChatFactory(), new SubAgentTestFakes.FakeSubAgentTable());
        session.EnqueueEvent(new SessionIdleEvent { Data = new() });
        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "synthetic request")])) { }

        session.Emit(new ToolExecutionStartEvent
        {
            Data = new() { ToolCallId = "continued-call", ToolName = "read" },
        });
        session.Emit(new ToolExecutionCompleteEvent
        {
            Data = new() { ToolCallId = "continued-call", Success = true, Result = new() { Content = "continued result" } },
        });
        session.Emit(new AssistantMessageDeltaEvent
        {
            Data = new() { MessageId = "continued-answer", DeltaContent = "continued answer" },
        });
        session.Emit(new SessionIdleEvent { Data = new() });
        // A mistaken replay still terminates deterministically, but cannot masquerade as success.
        session.EnqueueEvent(new SessionIdleEvent { Data = new() });
        var options = new ChatOptions
        {
            AdditionalProperties = new() { ["copilot.sdk.consume-continuation"] = true },
        };
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "must not resend")], options))
            updates.Add(update);

        Assert.Equal(1, session.SendAsyncCallCount);
        Assert.Equal("continued-call", Assert.Single(updates.SelectMany(update => update.Contents).OfType<FunctionCallContent>()).CallId);
        Assert.Equal("continued-call", Assert.Single(updates.SelectMany(update => update.Contents).OfType<FunctionResultContent>()).CallId);
        Assert.Equal("continued answer", string.Concat(updates.SelectMany(update => update.Contents).OfType<TextContent>().Select(content => content.Text)));
    }
}
