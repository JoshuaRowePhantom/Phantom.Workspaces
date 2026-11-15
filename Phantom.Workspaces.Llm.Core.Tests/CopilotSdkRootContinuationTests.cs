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
    public async Task ParentDispose_SettlesDeferredRequestWithoutSendingItAfterAcceptedContinuation()
    {
        var native = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
        var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new SerializedStore
        {
            BeforeWriteAsync = async request =>
            {
                if (request.NewMessages?.Any(message => message.Text == "continuing") != true) return;
                entered.TrySetResult();
                await release.Task;
            },
        };
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        await using var chat = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = Guid.NewGuid().ToString(), AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(Definition),
            ConfiguredStore = store, ClientOverride = client, ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new() { RunningAgentChatFactory = new SubAgentTestFakes.FakeRunningAgentChatFactory() },
        });
        EnqueueTaskFinal(native, "initial", "initial final");
        var initial = WhenSettled(chat, "initial final");
        chat.EnqueueUserMessage("initial request");
        await initial;
        var accepted = false;
        chat.InputQueueManager.QueueStateChanged += (_, change) =>
        {
            if (change.ChangeKind != AgentInputQueueManager.QueueStateChangeKind.ItemRemoved || accepted) return;
            accepted = true;
            native.Emit(new AssistantMessageDeltaEvent { Data = new() { MessageId = "continuing", DeltaContent = "continuing" } });
            native.Emit(new AssistantMessageDeltaEvent { Data = new() { MessageId = "boundary", DeltaContent = "accepted boundary" } });
        };
        try
        {
            var completion = chat.EnqueueUserMessageWithCompletion("must not start");
            await entered.Task;
            Assert.False(completion.Settlement.IsCompleted);
            await chat.InterruptAsync();
            var disposal = chat.DisposeAsync().AsTask();
            release.TrySetResult();
            await disposal;
            await completion.Terminal;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.Settlement);
            Assert.Equal(1, native.SendAsyncCallCount);
            var messages = await store.ReadMessagesAsync(new() { AgentSessionId = chat.AgentSessionId });
            Assert.Single(messages, message => message.Text == "continuing");
            Assert.DoesNotContain(messages, message => message.Text == "must not start");
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ParentDispose_AwaitsEveryAbandonedReadAndRetainsEachIndependentFailure()
    {
        var firstFailure = new IOException("first abandoned provider read");
        var secondFailure = new InvalidDataException("second abandoned provider read");
        var resourceFailure = new InvalidOperationException("independent resource");
        var provider = new LateFailureClient(firstFailure, secondFailure);
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        var chat = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = Guid.NewGuid().ToString(), AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(Definition),
            ConfiguredStore = new SerializedStore(), ClientOverride = provider, ForegroundScheduler = scheduler.ExclusiveScheduler,
        });
        var resource = new FailingResource(resourceFailure);
        chat.RegisterOwnedResource(resource);
        Task? disposal = null;
        try
        {
            for (var index = 0; index < 2; index++)
            {
                chat.EnqueueUserMessage($"request-{index}");
                await provider.Started[index].Task;
                var interrupted = WhenSettled(chat, "Interrupted by user.");
                await chat.InterruptAsync();
                await interrupted;
            }
            disposal = chat.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, resource.Calls);
            provider.Release[0].TrySetResult();
            provider.Release[1].TrySetResult();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => disposal);
            Assert.Equal<Exception>([firstFailure, secondFailure],
                Assert.IsType<AggregateException>(failure.InnerExceptions[0]).InnerExceptions);
            Assert.Same(resourceFailure, failure.InnerExceptions[1]);
            Assert.Equal(3, failure.Flatten().InnerExceptions.Count);
            Assert.Equal(1, resource.Calls);
        }
        finally
        {
            foreach (var release in provider.Release) release.TrySetResult();
            if (disposal is not null)
                await disposal.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            else
                await chat.DisposeAsync();
        }
    }

    private sealed class LateFailureClient(params Exception[] failures) : IChatClient
    {
        private int invocation;
        internal TaskCompletionSource[] Started { get; } = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        internal TaskCompletionSource[] Release { get; } = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var index = Interlocked.Increment(ref this.invocation) - 1;
            yield return new() { Role = ChatRole.Assistant, Contents = [new TextContent($"response-{index}")] };
            this.Started[index].TrySetResult();
            await this.Release[index].Task;
            throw failures[index];
        }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentDispose_AwaitsAcceptedAppendAndRetainsIndependentOriginalFailures(bool failAppend)
    {
        var native = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
        var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appendFailure = new InvalidOperationException("owned accepted append failure");
        var firstFailure = new IOException("independent first cleanup failure");
        var secondFailure = new InvalidDataException("independent second cleanup failure");
        var store = new SerializedStore
        {
            BeforeWriteAsync = async request =>
            {
                if (request.NewMessages?.Any(message => message.Text == "gated append") != true) return;
                entered.TrySetResult();
                await release.Task;
                if (failAppend) throw appendFailure;
            },
        };
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        var chat = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = Guid.NewGuid().ToString(), AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(Definition),
            ConfiguredStore = store, ClientOverride = client, ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new() { RunningAgentChatFactory = new SubAgentTestFakes.FakeRunningAgentChatFactory() },
        });
        var first = new FailingResource(firstFailure);
        var second = new FailingResource(secondFailure);
        chat.RegisterOwnedResource(first);
        chat.RegisterOwnedResource(second);
        native.EnqueueEvent(new AssistantMessageDeltaEvent { Data = new() { MessageId = "gated", DeltaContent = "gated append" } });
        native.EnqueueEvent(new ToolExecutionStartEvent { Data = new() { ToolCallId = "read", ToolName = "read" } });
        native.EnqueueEvent(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "read", Success = true } });
        chat.EnqueueUserMessage("request");
        await entered.Task;
        await chat.InterruptAsync();
        var disposal = chat.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposal.IsCompleted);
            Assert.Equal(0, first.Calls);
            Assert.Equal(0, second.Calls);
            Assert.True(native.SubscriberCount > 0);
            release.TrySetResult();
            var failure = await Assert.ThrowsAsync<AggregateException>(() => disposal);
            var originals = failure.Flatten().InnerExceptions;
            Assert.Contains(originals, error => ReferenceEquals(error, firstFailure));
            Assert.Contains(originals, error => ReferenceEquals(error, secondFailure));
            if (failAppend) Assert.Contains(originals, error => ReferenceEquals(error, appendFailure));
            Assert.Equal(failAppend ? 3 : 2, originals.Count);
            Assert.Equal(1, first.Calls);
            Assert.Equal(1, second.Calls);
            Assert.Equal(0, native.SubscriberCount);
        }
        finally
        {
            release.TrySetResult();
            await disposal.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    private sealed class FailingResource(Exception failure) : IAsyncDisposable
    {
        internal int Calls { get; private set; }
        public ValueTask DisposeAsync()
        {
            this.Calls++;
            return ValueTask.FromException(failure);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentChat_InterruptedBeforeFirstProviderRead_DrainsOnlyExistingAcceptedOwnership(bool acceptedContinuation)
    {
        var native = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
        var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
        var store = new SerializedStore();
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        var product = Guid.NewGuid().ToString();
        await using var chat = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = product, AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(Definition),
            ConfiguredStore = store, ClientOverride = client, ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new() { RunningAgentChatFactory = new SubAgentTestFakes.FakeRunningAgentChatFactory() },
        });
        if (acceptedContinuation)
        {
            EnqueueTaskFinal(native, "initial", "initial final");
            var initial = WhenSettled(chat, "initial final");
            chat.EnqueueUserMessage("initial request");
            await initial;
        }
        ((System.Collections.Specialized.INotifyCollectionChanged)chat.RunningItems).CollectionChanged += (_, _) =>
        {
            if (chat.RunningItems.Count > 0) _ = chat.InterruptAsync();
        };
        var finished = WhenSettled(chat, "Interrupted by user.");
        if (acceptedContinuation)
            await Task.Factory.StartNew(() => EmitTaskFinal(native, "accepted", "accepted before cancelled first read"),
                CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler.ExclusiveScheduler);
        else chat.EnqueueUserMessage("must not reach SDK");
        await finished;
        await chat.WaitForProviderCleanupAsync();
        Assert.Equal(acceptedContinuation ? 1 : 0, native.SendAsyncCallCount);
        Assert.Equal(acceptedContinuation ? 1 : 0, native.CreateSessionConfigs.Count);
        Assert.Empty(native.ResumeSessionCalls);
        if (acceptedContinuation)
        {
            var messages = await store.ReadMessagesAsync(new() { AgentSessionId = product });
            Assert.Single(messages, message => message.Text == "accepted before cancelled first read");
            var live = chat.History.SelectMany(item => item.Contents).OfType<TextContent>().Select(text => text.Text).ToArray();
            Assert.True(Array.IndexOf(live, "accepted before cancelled first read") < Array.IndexOf(live, "Interrupted by user."));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentChat_AcceptedAdmissionComposed_InMemoryRestoresExactlyOnce(bool continuationBoundary)
    {
        var store = new SerializedStore();
        await CopilotSdkRetirementPersistenceScenario.RunAdmissionAsync(() => store, continuationBoundary);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ImmediateSteeringSendFails_ReportsUndeliveredInput(bool asynchronousSend, bool observerFails)
    {
        var native = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
        var queues = new AgentInputQueueManager();
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null, queueManager: queues);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
        client.SetSubAgentDependencies(new SubAgentTestFakes.FakeRunningAgentChatFactory(), new SubAgentTestFakes.FakeSubAgentTable());
        native.EnqueueEvent(new AssistantMessageDeltaEvent { Data = new() { MessageId = "active", DeltaContent = "active" } });
        await using var stream = client.GetStreamingResponseAsync([new(ChatRole.User, "request")]).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        var failure = new InvalidOperationException("independent native steering send failure");
        var sendCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (asynchronousSend) native.SendHandler = (_, _) => sendCompletion.Task;
        else native.Sending = count => { if (count == 2) throw failure; };
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SteeringDeliveryFailed += (_, error) => { reported.TrySetResult(error); return Task.CompletedTask; };
        var observerFailure = new IOException("independent delivery observer failure");
        if (observerFails) client.SteeringDeliveryFailed += (_, _) => Task.FromException(observerFailure);
        var completion = new AgentInputTurnCompletion();
        var message = new ChatMessage(ChatRole.User, "steering");
        queues.Enqueue(queues.ImmediateQueue, [new() { Messages = [message], TurnCompletion = completion }]);
        if (asynchronousSend)
        {
            Assert.False(reported.Task.IsCompleted);
            sendCompletion.SetException(failure);
        }
        Assert.Same(failure, await reported.Task);
        var outcome = Assert.Single(await client.WaitForSteeringDeliveriesAsync());
        Assert.False(outcome.Delivered);
        Assert.Same(message, outcome.Message);
        if (observerFails)
            Assert.Equal<Exception>([failure, observerFailure], Assert.IsType<AggregateException>(outcome.Failure).InnerExceptions);
        else Assert.Same(failure, outcome.Failure);
        Assert.Same(outcome.Failure, await Assert.ThrowsAnyAsync<Exception>(() => completion.Settlement));
        native.Emit(new SessionIdleEvent { Data = new() });
        while (await stream.MoveNextAsync()) { }
        Assert.Equal(2, native.SendAsyncCallCount);
        Assert.Empty(queues.ImmediateQueue.Items);
    }

    [Theory]
    [InlineData("replace-me")]
    [InlineData(null)]
    [InlineData("")]
    public async Task AgentChat_FinalReplacement_ReconcilesAlreadyPersistedAndPromotedMessageInPlace(string? nativeMessageId)
    {
        var native = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
        var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
        var store = new InMemoryAgentPersistenceStore();
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        var product = Guid.NewGuid().ToString();
        await using var chat = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = product, AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(Definition),
            ConfiguredStore = store, ClientOverride = client, ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new() { RunningAgentChatFactory = new SubAgentTestFakes.FakeRunningAgentChatFactory() },
        });
        var promoted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((System.Collections.Specialized.INotifyCollectionChanged)chat.History).CollectionChanged += (_, _) =>
        {
            if (chat.History.Any(item => item.Contents.OfType<TextContent>().Any(content => content.Text == "draft")))
                promoted.TrySetResult();
        };
        native.EnqueueEvent(new AssistantMessageDeltaEvent { Data = new() { MessageId = nativeMessageId!, DeltaContent = "draft" } });
        native.EnqueueEvent(new ToolExecutionStartEvent { Data = new() { ToolCallId = "read", ToolName = "read" } });
        native.EnqueueEvent(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "read", Success = true, Result = new() { Content = "read result" } } });
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = false;
        ((System.Collections.Specialized.INotifyCollectionChanged)chat.RunningItems).CollectionChanged += (_, _) =>
        {
            if (chat.RunningItems.Count > 0) started = true;
            else if (started) completed.TrySetResult();
        };
        chat.EnqueueUserMessage("request");
        await promoted.Task;
        var livePrefix = Assert.Single(chat.History, item => item.Contents.OfType<TextContent>().Any(content => content.Text == "draft"));
        var prefix = Assert.Single(await store.ReadMessagesAsync(new() { AgentSessionId = product }), message => message.Text == "draft");
        Assert.False(string.IsNullOrEmpty(prefix.MessageId));
        Assert.Equal(livePrefix.MessageId, prefix.MessageId);
        if (!string.IsNullOrEmpty(nativeMessageId)) Assert.Equal(nativeMessageId, prefix.MessageId);
        native.Emit(new AssistantMessageEvent { Data = new() { MessageId = nativeMessageId!, Content = "full replacement" } });
        native.Emit(new AssistantMessageEvent { Data = new() { MessageId = nativeMessageId!, Content = "full replacement" } });
        native.Emit(new AssistantMessageEvent { Data = new() { MessageId = "other", Content = "distinct final" } });
        native.Emit(new SessionIdleEvent { Data = new() });
        await completed.Task;
        var messages = await store.ReadMessagesAsync(new() { AgentSessionId = product });
        var replacement = Assert.Single(messages, message => message.MessageId == prefix.MessageId);
        Assert.Equal("full replacement", replacement.Text);
        Assert.Equal(prefix.CreatedAt, replacement.CreatedAt);
        var liveReplacement = Assert.Single(chat.History, item => item.MessageId == prefix.MessageId);
        Assert.Equal(livePrefix.Timestamp, liveReplacement.Timestamp);
        Assert.Equal(livePrefix.AssistantRunId, liveReplacement.AssistantRunId);
        Assert.Single(chat.History.SelectMany(item => item.Contents).OfType<TextContent>(), text => text.Text == "full replacement");
        Assert.DoesNotContain(chat.History.SelectMany(item => item.Contents).OfType<TextContent>(), text => text.Text.Contains("draft"));
        Assert.Single(messages, message => message.MessageId == "other");
    }

    [Fact]
    public async Task AgentChat_ContinuationAcceptedDuringRequestDequeue_PrecedesNewPromptAdmission()
    {
        var native = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
        var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
        var store = new SerializedStore();
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        var product = Guid.NewGuid().ToString();
        await using var chat = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = product, AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(Definition),
            ConfiguredStore = store, ClientOverride = client, ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new() { RunningAgentChatFactory = new SubAgentTestFakes.FakeRunningAgentChatFactory() },
        });
        EnqueueTaskFinal(native, "initial", "initial final");
        var first = WhenSettled(chat, "initial final");
        chat.EnqueueUserMessage("initial request");
        await first;
        var boundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        chat.InputQueueManager.QueueStateChanged += (_, change) =>
        {
            if (change.ChangeKind == AgentInputQueueManager.QueueStateChangeKind.ItemRemoved
                && boundary.TrySetResult())
                EmitTaskFinal(native, "between", "accepted between pending check and acquire");
        };
        native.Sending = count => { if (count == 2) EmitTaskFinal(native, "new", "new final"); };
        var second = WhenSettled(chat, "new final");
        chat.EnqueueUserMessage("new request");
        await boundary.Task;
        await second;
        var texts = (await store.ReadMessagesAsync(new() { AgentSessionId = product })).Select(message => message.Text).ToArray();
        Assert.True(Array.IndexOf(texts, "accepted between pending check and acquire") < Array.IndexOf(texts, "new request"));
        var live = chat.History.SelectMany(item => item.Contents).OfType<TextContent>().Select(content => content.Text).ToArray();
        Assert.True(Array.IndexOf(live, "accepted between pending check and acquire") < Array.IndexOf(live, "new request"));
        Assert.Equal(2, native.SendAsyncCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentChat_InterruptedAcceptedSuffix_IsVisibleAndDurableBeforeNextRequestOrDisposal(bool dispose)
    {
        var native = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
        var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new SerializedStore
        {
            BeforeWriteAsync = async request =>
            {
                if (request.NewMessages?.Any(message => message.Text == "old accepted prefix") == true)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
            },
        };
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        var product = Guid.NewGuid().ToString();
        var chat = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = product, AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(Definition),
            ConfiguredStore = store, ClientOverride = client, ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new() { RunningAgentChatFactory = new SubAgentTestFakes.FakeRunningAgentChatFactory() },
        });
        native.EnqueueEvent(new AssistantMessageDeltaEvent { Data = new() { MessageId = "old-prefix", DeltaContent = "old accepted prefix" } });
        native.EnqueueEvent(new ToolExecutionStartEvent { Data = new() { ToolCallId = "old-call", ToolName = "read" } });
        native.EnqueueEvent(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "old-call", Success = true, Result = new() { Content = "old result" } } });
        chat.EnqueueUserMessage("old request");
        await entered.Task;
        EmitTaskFinal(native, "old-suffix", "old accepted final");
        await chat.InterruptAsync();
        Task? disposal = null;
        Task? settled = null;
        if (dispose)
        {
            disposal = chat.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            Assert.True(native.SubscriberCount > 0);
        }
        else
        {
            native.Sending = count => { if (count == 2) EmitTaskFinal(native, "new-call", "new final"); };
            settled = WhenSettled(chat, "new final");
            chat.EnqueueUserMessage("new request");
        }
        release.TrySetResult();
        if (disposal is not null) await disposal;
        else await settled!;
        var texts = chat.History.SelectMany(item => item.Contents).OfType<TextContent>().Select(content => content.Text).ToArray();
        Assert.Contains("old accepted final", texts);
        Assert.Single(chat.History.SelectMany(item => item.Contents).OfType<FunctionResultContent>(), result => result.CallId == "old-suffix");
        if (!dispose)
        {
            Assert.True(Array.IndexOf(texts, "old accepted final") < Array.IndexOf(texts, "Interrupted by user."));
            Assert.True(Array.IndexOf(texts, "Interrupted by user.") < Array.IndexOf(texts, "new request"));
        }
        var durable = await store.ReadMessagesAsync(new() { AgentSessionId = product });
        var persistedText = durable.Select(message => message.Text).ToArray();
        Assert.Contains("old accepted final", persistedText);
        if (!dispose) Assert.True(Array.IndexOf(persistedText, "old accepted final") < Array.IndexOf(persistedText, "new request"));
        await chat.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCancelledBeforeFirstRead_DoesNotCreateResumeOrSendNativeWork(bool resume)
    {
        var native = new FakeCopilotSession();
        await using var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
        if (resume) client.SetResumeSessionId(Guid.NewGuid().ToString());
        var store = new SerializedStore();
        var provider = new IncrementalPersistenceChatHistoryProvider(AgentDefinitionLoader.LoadAgentFromJson(Definition), store);
        var middleware = new StreamingPersistenceMiddleware(client, provider, store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var output = middleware.GetStreamingResponseAsync([new(ChatRole.User, "must not send")],
            cancellationToken: cancellation.Token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await output.MoveNextAsync());
        Assert.Equal(0, native.SendAsyncCallCount);
        Assert.Empty(native.CreateSessionConfigs);
        Assert.Empty(native.ResumeSessionCalls);
        Assert.Equal(0, native.SubscriberCount);
    }

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
        var foreground = new ConcurrentExclusiveSchedulerPair();
        var store = new SerializedStore
        {
            BeforeStoreAsync = async () =>
            {
                Assert.NotSame(foreground.ExclusiveScheduler, TaskScheduler.Current);
                entered.TrySetResult();
                await release.Task;
            },
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
        await Task.Factory.StartNew(async () => await output.DisposeAsync(), CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, foreground.ExclusiveScheduler).Unwrap();
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
        public Func<StoreRequestAgent, Task>? BeforeWriteAsync { get; init; }
        public async ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
        {
            if (this.BeforeStoreAsync is { } before) await before();
            if (this.BeforeWriteAsync is { } write) await write(request);
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
