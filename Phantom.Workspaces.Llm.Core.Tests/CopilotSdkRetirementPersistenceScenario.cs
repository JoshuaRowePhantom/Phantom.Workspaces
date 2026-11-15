using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AgentSchema;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Core.Tests.Infrastructure;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Core.Tests;

internal static partial class CopilotSdkRetirementPersistenceScenario
{
    private const string Definition = """
        {"kind":"prompt","name":"retirement-root","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
        """;

    internal static async Task<string> RunAdmissionAsync(Func<IAgentPersistenceStore> freshStore, bool continuationBoundary, bool afterReservation = false)
    {
        var productId = Guid.NewGuid().ToString();
        var nativeId = Guid.NewGuid().ToString();
        var native = new FakeCopilotSession { SessionId = nativeId };
        var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        var factory = new RetirementFactory(scheduler.ExclusiveScheduler);
        var gatedStore = new AdmissionStore(freshStore());
        await using var parent = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = productId,
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(Definition),
            ConfiguredStore = gatedStore, ClientOverride = client,
            ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new() { RunningAgentChatFactory = factory },
        });
        try
        {
            if (continuationBoundary)
            {
                native.Sending = count => EmitTaskFinal(native, count == 1 ? "initial" : "new", count == 1 ? "initial final" : "new final");
                var initial = WhenSettledAsync(parent, "initial final");
                parent.EnqueueUserMessage("initial request");
                await initial;
                var emitted = false;
                void AcceptContinuation()
                {
                    if (emitted) return;
                    emitted = true;
                    EmitTaskFinal(native, "between", "old accepted final");
                }
                parent.InputQueueManager.QueueStateChanged += (_, change) =>
                {
                    if (!afterReservation && change.ChangeKind == AgentInputQueueManager.QueueStateChangeKind.ItemRemoved)
                        AcceptContinuation();
                };
                ((INotifyCollectionChanged)parent.RunningItems).CollectionChanged += (_, _) =>
                {
                    if (afterReservation && parent.RunningItems.Count > 0) AcceptContinuation();
                };
            }
            else
            {
                native.Sending = count =>
                {
                    if (count != 1) { EmitTaskFinal(native, "new", "new final"); return; }
                    native.Emit(new AssistantMessageDeltaEvent { Data = new() { MessageId = "old-prefix", DeltaContent = "gated old prefix" } });
                    native.Emit(new ToolExecutionStartEvent { Data = new() { ToolCallId = "initial", ToolName = "read" } });
                    native.Emit(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "initial", Success = true, Result = new() { Content = "initial result" } } });
                };
                parent.EnqueueUserMessage("initial request");
                await gatedStore.Entered.Task;
                EmitTaskFinal(native, "old", "old accepted final");
                await parent.InterruptAsync();
            }
            var finished = WhenSettledAsync(parent, "new final");
            parent.EnqueueUserMessage("new request");
            if (!continuationBoundary)
                Assert.DoesNotContain(await freshStore().ReadMessagesAsync(new() { AgentSessionId = productId }), message => message.Text == "new request");
            gatedStore.Release.TrySetResult();
            await finished;
            var messages = await freshStore().ReadMessagesAsync(new() { AgentSessionId = productId });
            AssertTranscript(messages, "new final", 3);
            Assert.True(Array.FindIndex(messages, message => message.Text == "old accepted final")
                < Array.FindIndex(messages, message => message.Text == "new request"));
            var live = parent.History.SelectMany(item => item.Contents).OfType<TextContent>().Select(text => text.Text).ToArray();
            Assert.True(Array.IndexOf(live, "old accepted final") < Array.IndexOf(live, "new request"));
            var oldRun = Assert.Single(parent.History, item => item.Contents.OfType<TextContent>().Any(text => text.Text == "old accepted final")).AssistantRunId;
            var newRun = Assert.Single(parent.History, item => item.Contents.OfType<TextContent>().Any(text => text.Text == "new final")).AssistantRunId;
            Assert.NotNull(oldRun);
            Assert.NotEqual(oldRun, newRun);
            Assert.Equal(nativeId, (await freshStore().RestoreAsync(new() { AgentSessionId = productId }))!.Value.CopilotSdkSessionId);
            Assert.Empty(await freshStore().ReadMessagesAsync(new() { AgentSessionId = nativeId }));
            Assert.Equal(2, native.SendAsyncCallCount);
            await parent.DisposeAsync();
            await using var restored = await AgentChat.CreateAsync(new()
            {
                AgentSessionId = productId, AgentDefinition = null,
                ConfiguredStore = freshStore(), ClientOverride = new CopilotSubAgentChatClient(),
                ForegroundScheduler = scheduler.ExclusiveScheduler,
            });
            Assert.Single(restored.History.SelectMany(item => item.Contents).OfType<TextContent>(), text => text.Text == "old accepted final");
            Assert.Equal(3, restored.History.SelectMany(item => item.Contents).OfType<FunctionCallContent>().Count());
            return productId;
        }
        finally { gatedStore.Release.TrySetResult(); }
    }

    internal static async Task<string> RunPendingShutdownAsync(Func<IAgentPersistenceStore> freshStore)
    {
        var productId = Guid.NewGuid().ToString();
        var native = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
        var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        var store = new AdmissionStore(freshStore());
        var parent = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = productId, AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(Definition),
            ConfiguredStore = store, ClientOverride = client, ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new() { RunningAgentChatFactory = new RetirementFactory(scheduler.ExclusiveScheduler) },
        });
        Task? disposal = null;
        try
        {
            native.Sending = _ =>
            {
                native.Emit(new AssistantMessageDeltaEvent { Data = new() { MessageId = "prefix", DeltaContent = "gated old prefix" } });
                EmitTaskFinal(native, "first", "first accepted final");
            };
            parent.EnqueueUserMessage("first request");
            await store.Entered.Task;
            EmitTaskFinal(native, "second", "second accepted final");
            Assert.True(client.HasPendingRootContinuation);
            parent.EnqueueUserMessage("never send");
            disposal = parent.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
            store.Release.TrySetResult();
            await disposal;
            var messages = await freshStore().ReadMessagesAsync(new() { AgentSessionId = productId });
            AssertTranscript(messages, "second accepted final", 2);
            Assert.Single(messages, message => message.Text == "first accepted final");
            Assert.DoesNotContain(messages, message => message.Text == "never send");
            Assert.Single(parent.History.SelectMany(item => item.Contents).OfType<TextContent>(), text => text.Text == "second accepted final");
            Assert.Equal(1, native.SendAsyncCallCount);
            Assert.Empty(parent.RunningItems);
            Assert.Equal(0, native.SubscriberCount);
            Assert.Equal(native.SessionId, (await freshStore().RestoreAsync(new() { AgentSessionId = productId }))!.Value.CopilotSdkSessionId);
            return productId;
        }
        finally
        {
            store.Release.TrySetResult();
            if (disposal is not null) await disposal.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            else await parent.DisposeAsync();
        }
    }

    internal static async Task<string> RunCorrectionAsync(
        Func<IAgentPersistenceStore> freshStore,
        bool prefixExtension,
        int steering,
        Func<Task>? beforeFinal = null)
    {
        var productId = Guid.NewGuid().ToString();
        var native = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
        var steeringQueue = new AgentInputQueueManager();
        var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null, queueManager: steeringQueue);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        await using var parent = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = productId, AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(Definition),
            ConfiguredStore = freshStore(), ClientOverride = client, ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new() { RunningAgentChatFactory = new RetirementFactory(scheduler.ExclusiveScheduler) },
        });
        var promoted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)parent.History).CollectionChanged += (_, _) =>
        {
            if (parent.History.Any(item => item.MessageId == "corrected")) promoted.TrySetResult();
        };
        native.EnqueueEvent(new AssistantMessageDeltaEvent { Data = new() { MessageId = "corrected", DeltaContent = "draft" } });
        native.EnqueueEvent(new ToolExecutionStartEvent { Data = new() { ToolCallId = "read", ToolName = "read" } });
        native.EnqueueEvent(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "read", Success = true, Result = new() { Content = "intervening result" } } });
        parent.EnqueueUserMessage("correction request");
        await promoted.Task;
        var original = await Task.Factory.StartNew(() =>
        {
            var item = Assert.Single(parent.History, item => item.MessageId == "corrected");
            return (item.Timestamp, item.AssistantRunId);
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler.ExclusiveScheduler);
        var before = await freshStore().ReadMessagesAsync(new() { AgentSessionId = productId });
        var originalMessage = Assert.Single(before, message => message.MessageId == "corrected");
        if (beforeFinal is not null) await beforeFinal();
        var sendFailure = new InvalidOperationException("synthetic delivery failure");
        if (steering != 0)
        {
            native.SendHandler = (_, _) => steering == 2 ? Task.FromException(sendFailure) : Task.CompletedTask;
            steeringQueue.Enqueue(steeringQueue.ImmediateQueue,
                [new AgentInputItem { Messages = [new ChatMessage(ChatRole.User, "interstitial steering")] }]);
            var outcome = Assert.Single(await client.WaitForSteeringDeliveriesAsync());
            Assert.Equal(steering == 1, outcome.Delivered);
            if (steering == 2) Assert.Same(sendFailure, outcome.Failure);
        }
        var text = prefixExtension ? "draft completed" : "authoritative replacement";
        var settled = await Task.Factory.StartNew(() => WhenSettledAsync(parent, "distinct final"),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler.ExclusiveScheduler);
        native.Emit(new AssistantMessageEvent { Data = new() { MessageId = "corrected", Content = text } });
        native.Emit(new AssistantMessageEvent { Data = new() { MessageId = "corrected", Content = text } });
        native.Emit(new AssistantMessageEvent { Data = new() { MessageId = "distinct", Content = "distinct final" } });
        native.Emit(new SessionIdleEvent { Data = new() });
        await settled;
        var after = await freshStore().ReadMessagesAsync(new() { AgentSessionId = productId });
        var corrected = Assert.Single(after, message => message.MessageId == "corrected");
        Assert.Equal(text, corrected.Text);
        Assert.Equal(originalMessage.CreatedAt, corrected.CreatedAt);
        Assert.Equal(Array.FindIndex(before, message => message.MessageId == "corrected"), Array.FindIndex(after, message => message.MessageId == "corrected"));
        await Task.Factory.StartNew(() =>
        {
            var live = Assert.Single(parent.History, item => item.MessageId == "corrected");
            Assert.Equal(original.Timestamp, live.Timestamp);
            Assert.Equal(original.AssistantRunId, live.AssistantRunId);
            Assert.Equal(text, string.Concat(live.Contents.OfType<TextContent>().Select(content => content.Text)));
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler.ExclusiveScheduler);
        Assert.Equal(steering == 0 ? 1 : 2, native.SendAsyncCallCount);
        await using var restored = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = productId, AgentDefinition = null, ConfiguredStore = freshStore(),
            ClientOverride = new CopilotSubAgentChatClient(), ForegroundScheduler = scheduler.ExclusiveScheduler,
        });
        await Task.Factory.StartNew(() =>
            Assert.Equal(text, string.Concat(Assert.Single(restored.History, item => item.MessageId == "corrected").Contents.OfType<TextContent>().Select(content => content.Text))),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler.ExclusiveScheduler);
        return productId;
    }

    private sealed class AdmissionStore(IAgentPersistenceStore inner) : IAgentPersistenceStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask StoreAsync(StoreRequestAgent request, CancellationToken cancellationToken = default)
        {
            if (request.NewMessages?.Any(message => message.Text == "gated old prefix") == true)
            {
                this.Entered.TrySetResult();
                await this.Release.Task;
            }
            await inner.StoreAsync(request, cancellationToken);
        }
        public ValueTask<PersistedAgent?> RestoreAsync(RestoreRequest request, CancellationToken cancellationToken = default) => inner.RestoreAsync(request, cancellationToken);
        public ValueTask<ChatMessage[]> ReadMessagesAsync(ReadMessagesRequest request, CancellationToken cancellationToken = default) => inner.ReadMessagesAsync(request, cancellationToken);
        public ValueTask AddSubAgentLinkAsync(string parentSessionId, string childSessionId, CancellationToken cancellationToken = default) => inner.AddSubAgentLinkAsync(parentSessionId, childSessionId, cancellationToken);
        public ValueTask<IReadOnlyList<AgentSessionId>> ReadSubAgentChildIdsAsync(string parentSessionId, CancellationToken cancellationToken = default) => inner.ReadSubAgentChildIdsAsync(parentSessionId, cancellationToken);
    }

    internal static async Task RunAsync(Func<IAgentPersistenceStore> freshStore)
    {
        var productId = Guid.NewGuid().ToString();
        var nativeId = Guid.NewGuid().ToString();
        var session = new FakeCopilotSession { SessionId = nativeId };
        var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(session)));
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        var factory = new RetirementFactory(scheduler.ExclusiveScheduler);
        await using var parent = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = productId,
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(Definition),
            ConfiguredStore = freshStore(),
            ClientOverride = client,
            ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new() { RunningAgentChatFactory = factory },
        });
        var timestamp = DateTimeOffset.UtcNow;
        session.EnqueueEvent(new ToolExecutionStartEvent { Data = new() { ToolCallId = "child-call", ToolName = "task" } });
        session.EnqueueEvent(new SubagentStartedEvent
        {
            Id = Guid.NewGuid(), AgentId = "child", Timestamp = timestamp,
            Data = new() { ToolCallId = "child-call", AgentName = "task", AgentDisplayName = "Task", AgentDescription = "synthetic child" },
        });
        session.EnqueueEvent(new AssistantMessageDeltaEvent { Data = new() { MessageId = "registered", DeltaContent = "registered child" } });
        var registered = WhenRunningTextAsync(parent, "registered child");
        var settled = WhenSettledAsync(parent, "durable while retiring");
        parent.EnqueueUserMessage("first request");
        await factory.Created.Task;
        await registered;
        try
        {
            session.Emit(new SubagentCompletedEvent
            {
                Id = Guid.NewGuid(), AgentId = "child", Timestamp = timestamp.AddSeconds(1),
                Data = new() { ToolCallId = "child-call", AgentName = "task", AgentDisplayName = "Task" },
            });
            await factory.Retiring.Task;
            session.Emit(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "child-call", Success = true, Result = new() { Content = "child done" } } });
            EmitTaskFinal(session, "following-call", "durable while retiring");
            await settled;
            Assert.False(factory.Release.Task.IsCompleted);
            var durable = await freshStore().ReadMessagesAsync(new() { AgentSessionId = productId });
            AssertTranscript(durable, "durable while retiring", 2);
            Assert.Equal(nativeId, (await freshStore().RestoreAsync(new() { AgentSessionId = productId }))!.Value.CopilotSdkSessionId);
            Assert.Empty(await freshStore().ReadMessagesAsync(new() { AgentSessionId = nativeId }));
            var continuation = WhenSettledAsync(parent, "notification final");
            EmitTaskFinal(session, "notification-call", "notification final");
            await continuation;
            Assert.False(factory.Release.Task.IsCompleted);
            Assert.Equal(1, session.SendAsyncCallCount);
            AssertTranscript(await freshStore().ReadMessagesAsync(new() { AgentSessionId = productId }), "notification final", 3);
        }
        finally
        {
            factory.Release.TrySetResult();
        }
        await parent.DisposeAsync();
        await using var restored = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = productId,
            AgentDefinition = null,
            ConfiguredStore = freshStore(),
            ClientOverride = new CopilotSubAgentChatClient(),
            ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new() { RunningAgentChatFactory = factory },
        });
        Assert.Equal(3, restored.History.SelectMany(item => item.Contents).OfType<FunctionCallContent>().Count());
        Assert.Equal(3, restored.History.SelectMany(item => item.Contents).OfType<FunctionResultContent>().Count());
        Assert.Contains(restored.History, item => item.Contents.OfType<TextContent>().Any(text => text.Text == "durable while retiring"));
        Assert.Equal(1, factory.Releases);
        if (factory.Child is { } child) await child.DisposeAsync();
    }

    private static void AssertTranscript(ChatMessage[] messages, string final, int calls)
    {
        var contents = messages.SelectMany(message => message.Contents).ToArray();
        var functions = contents.OfType<FunctionCallContent>().ToArray();
        var results = contents.OfType<FunctionResultContent>().ToArray();
        Assert.Equal(calls, functions.Length);
        Assert.Equal(calls, results.Length);
        Assert.Equal(calls, functions.Select(call => call.CallId).Distinct().Count());
        Assert.Equal(functions.Select(call => call.CallId), results.Select(result => result.CallId));
        Assert.Single(contents.OfType<TextContent>(), text => text.Text == final);
        Assert.Equal(final, messages[^1].Text);
    }

    private static void EmitTaskFinal(FakeCopilotSession session, string call, string text)
    {
        session.Emit(new ToolExecutionStartEvent { Data = new() { ToolCallId = call, ToolName = "task" } });
        session.Emit(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = call, Success = true, Result = new() { Content = "completed result" } } });
        session.Emit(new AssistantMessageEvent { Data = new() { MessageId = call + "-final", Content = text } });
        session.Emit(new SessionIdleEvent { Data = new() });
    }

    internal static Task WhenSettledAsync(AgentChat chat, string text)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (chat.RunningItems.Count != 0 || !chat.History.Any(item => item.Contents.OfType<TextContent>().Any(content => content.Text == text))) return;
            ((INotifyCollectionChanged)chat.History).CollectionChanged -= Changed;
            ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged -= Changed;
            completion.TrySetResult();
        }
        ((INotifyCollectionChanged)chat.History).CollectionChanged += Changed;
        ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged += Changed;
        return completion.Task;
    }

    internal static Task WhenRunningTextAsync(AgentChat chat, string text)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new HashSet<AgentChatRunningItem>();
        void Changed(object? sender, NotifyCollectionChangedEventArgs args)
        {
            foreach (var run in chat.RunningItems)
                if (observed.Add(run)) run.Items.CollectionChanged += Changed;
            if (!chat.RunningItems.Any(run => run.Items.Any(item => item.Contents.OfType<TextContent>().Any(content => content.Text == text)))) return;
            ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged -= Changed;
            foreach (var run in observed) run.Items.CollectionChanged -= Changed;
            completion.TrySetResult();
        }
        ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged += Changed;
        Changed(null, new(NotifyCollectionChangedAction.Reset));
        return completion.Task;
    }

    private sealed class RetirementFactory(TaskScheduler scheduler) : IRunningAgentChatFactory
    {
        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = [];
        public TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Retiring { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentChat? Child { get; private set; }
        public RunningAgentChatLease? Lease { get; private set; }
        public int Releases { get; private set; }
        public Func<Task>? BeforeCreateAsync { get; set; }
        public Func<ValueTask>? ReleaseAsync { get; set; }
        public List<RunningAgentChatLease> CreatedLeases { get; } = [];
        public async Task<RunningAgentChatLease> CreateAsync(AgentDefinition definition, AgentSessionId sessionId,
            AgentServices? services, string? displayNameOverride, string? descriptionOverride, string? nameOverride, CancellationToken ct)
        {
            if (this.BeforeCreateAsync is { } beforeCreate) await beforeCreate();
            this.Child = await AgentChat.CreateAsync(new()
            {
                AgentSessionId = sessionId.Value,
                AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(Definition),
                ConfiguredStore = new InMemoryAgentPersistenceStore(),
                ClientOverride = new CopilotSubAgentChatClient(),
                ForegroundScheduler = scheduler,
            });
            this.Lease = new(sessionId, this.Child, async () =>
            {
                this.Releases++;
                this.Retiring.TrySetResult();
                await this.Release.Task;
                if (this.ReleaseAsync is { } release) await release();
            });
            this.CreatedLeases.Add(this.Lease);
            this.Created.TrySetResult();
            return this.Lease;
        }
        public Task<RunningAgentChatLease> GetAsync(AgentSessionId id, bool registerAsRunningAgent, CancellationToken ct) =>
            Task.FromResult(this.Lease!);
        public Task<RunningAgentChatLease> GetOrCreateAsync(AgentSessionId id, AgentDefinition? definition, AgentServices? services,
            string? displayNameOverride, string? descriptionOverride, bool registerAsRunningAgent, CancellationToken ct) =>
            Task.FromResult(this.Lease!);
    }
}
