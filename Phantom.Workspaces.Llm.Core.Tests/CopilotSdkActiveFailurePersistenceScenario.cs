using System.Collections.Specialized;
using System.Reflection;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Llm.Core.Tests.Infrastructure;
using Phantom.Workspaces.Llm.Interfaces;

#pragma warning disable GHCP001
namespace Phantom.Workspaces.Llm.Core.Tests;

internal static partial class CopilotSdkRetirementPersistenceScenario
{
    private sealed class ActiveObservationClock : FakeTimeProvider
    {
        public TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            this.Created.TrySetResult();
            return timer;
        }
    }

    internal static async Task<string> RunActiveFailureDrainAsync(
        Func<IAgentPersistenceStore> freshStore, string entry, bool cleanupFailure = false)
    {
        var productId = Guid.NewGuid().ToString();
        var native = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
        var nativeClient = new FakeCopilotClient(native);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        native.Sending = _ => requested.TrySetResult();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception original = entry == "dispatch"
            ? new InvalidOperationException("original active root dispatch failure")
            : new IOException("original active root observation failure");
        var secondary = new InvalidOperationException("independent accepted-batch routing failure");
        var disposalFailure = new IOException("independent old native disposal failure");
        var foreground = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        var factory = new RetirementFactory(foreground);
        factory.Release.TrySetResult();
        var clock = new ActiveObservationClock();
        AgentChat? owner = null;
        CopilotSdkChatClient? sdk = null;
        var parent = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = productId,
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"active-failure-parent","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
                """),
            ConfiguredStore = freshStore(),
            ForegroundScheduler = foreground,
            ChatClientFactoryOverride = _ =>
            {
                sdk = new("gpt-5", "Copilot", null, null, queueManager: owner!.InputQueueManager);
                sdk.StateObservationTimeProvider = clock;
                sdk.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(nativeClient));
                return Task.FromResult<IChatClient>(sdk);
            },
            AgentServices = new() { RunningAgentChatFactory = factory },
        }, constructed => owner = constructed);
        var retired = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)parent.RunningItems).CollectionChanged += (_, _) =>
        {
            if (parent.RunningItems.Count == 0) retired.TrySetResult(parent.DisposeAsync().AsTask());
        };
        try
        {
            var input = parent.EnqueueUserMessageWithCompletion("only native request");
            await requested.Task;
            if (entry is "retirement" or "poller")
            {
                await ObserveActiveAsync(sdk!, native, ActiveStart());
                await factory.Created.Task;
                if (entry == "retirement") factory.ReleaseAsync = () => ValueTask.FromException(original);
            }
            var creates = 0;
            factory.BeforeCreateAsync = async () =>
            {
                if (Interlocked.Increment(ref creates) == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                    if (entry == "dispatch") throw original;
                }
                else if (entry == "secondary-dispatch") throw secondary;
            };
            native.Emit(new AssistantMessageDeltaEvent { Data = new() { MessageId = "active-prefix", DeltaContent = "active accepted prefix" } });
            native.Emit(new SubagentStartedEvent
            {
                Id = Guid.NewGuid(), AgentId = "routing-gate", Timestamp = ActiveTime.AddSeconds(2),
                Data = new() { ToolCallId = "routing-gate-call", AgentName = "task", AgentDisplayName = "Routing gate", AgentDescription = "gated routing" },
            });
            await entered.Task;
            EmitBatch("active", "active accepted final");
            EmitBatch("pending", "pending accepted final", entry == "secondary-dispatch" ? () =>
                native.Emit(new SubagentStartedEvent
                {
                    Id = Guid.NewGuid(), AgentId = "failed-routing", Timestamp = ActiveTime.AddSeconds(3),
                    Data = new() { ToolCallId = "failed-routing-call", AgentName = "task", AgentDisplayName = "Failed routing", AgentDescription = "independent failure" },
                }) : null);
            EmitBatch("last", "last accepted final");
            Assert.True(sdk!.HasPendingRootContinuation);
            Assert.False(input.Settlement.IsCompleted);
            Assert.Equal(0, native.DisposeAsyncCallCount);
            native.OnDisposeAsync = async () =>
            {
                var durableAtRetirement = await freshStore().ReadMessagesAsync(new() { AgentSessionId = productId });
                Assert.Single(durableAtRetirement, message => message.Text == "last accepted final");
                if (cleanupFailure) throw disposalFailure;
            };
            if (entry is "observer" or "secondary-dispatch")
            {
                native.ListTasksHandler = _ => Task.FromException<IReadOnlyList<GitHub.Copilot.Rpc.TaskInfo>>(original);
                native.Emit(new SessionBackgroundTasksChangedEvent { Data = new() });
                await (Task)typeof(CopilotSdkChatClient).GetField("stateObserverPump", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sdk)!;
            }
            else if (entry == "poller")
            {
                await clock.Created.Task;
                nativeClient.LiveSessionFailure = original;
                clock.Advance(TimeSpan.FromSeconds(1));
                await (Task)typeof(CopilotSdkChatClient).GetField("liveSessionPoller", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sdk)!;
            }
            else if (entry == "retirement")
            {
                await ObserveActiveAsync(sdk, native, ActiveFinish());
                var tasks = (List<Task>)typeof(CopilotSdkChatClient)
                    .GetField("childRetirementTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sdk)!;
                Assert.Same(original, await Record.ExceptionAsync(() => Assert.Single(tasks)));
            }
            parent.EnqueueUserMessage("never admit deferred input");
            var immediate = new AgentInputTurnCompletion();
            if (entry != "dispatch")
            {
                parent.InputQueueManager.Enqueue(parent.InputQueueManager.ImmediateQueue,
                    [new() { Messages = [new ChatMessage(ChatRole.User, "never steer failed root")], TurnCompletion = immediate }]);
                Assert.Same(immediate, Assert.Single(parent.InputQueueManager.ImmediateQueue.Items).TurnCompletion);
            }
            release.TrySetResult();
            var failure = await Record.ExceptionAsync(() => input.Settlement);
            var leaves = failure is AggregateException aggregate ? aggregate.Flatten().InnerExceptions.ToArray() : [failure!];
            Assert.Contains(original, leaves);
            if (entry == "secondary-dispatch") Assert.Contains(secondary, leaves);
            if (cleanupFailure) Assert.Contains(disposalFailure, leaves);
            Assert.All(leaves, value => Assert.True(ReferenceEquals(value, original)
                || entry == "secondary-dispatch" && ReferenceEquals(value, secondary)
                || cleanupFailure && ReferenceEquals(value, disposalFailure), value.ToString()));
            var parentCleanup = await Record.ExceptionAsync(() => retired.Task.Unwrap());
            Assert.NotNull(parentCleanup);
            var cleanupLeaves = parentCleanup is AggregateException cleanupAggregate
                ? cleanupAggregate.Flatten().InnerExceptions.ToArray() : [parentCleanup];
            Assert.Contains(original, cleanupLeaves);
            Assert.All(cleanupLeaves, value => Assert.True(leaves.Contains(value), value.ToString()));
            var durable = await freshStore().ReadMessagesAsync(new() { AgentSessionId = productId });
            var ids = new[] { "active-prefix", "active", "pending", "last" };
            Assert.Equal(ids, durable.Where(message => ids.Contains(message.MessageId)).Select(message => message.MessageId));
            Assert.Equal(3, durable.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Count());
            Assert.Equal(3, durable.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count());
            Assert.DoesNotContain(durable, message => message.Text == "never admit deferred input");
            var live = parent.History.Where(item => ids.Contains(item.MessageId)).ToArray();
            Assert.Equal(ids, live.Select(item => item.MessageId));
            Assert.Single(live.Select(item => item.AssistantRunId).Distinct());
            Assert.NotNull(live[0].AssistantRunId);
            Assert.Equal(1, native.SendAsyncCallCount);
            Assert.Equal(1, native.DisposeAsyncCallCount);
            Assert.Equal(0, native.SubscriberCount);
            Assert.False(sdk.HasPendingRootContinuation);
            Assert.Empty(parent.RunningItems);
            if (entry != "dispatch")
            {
                Assert.Same(immediate, Assert.Single(parent.InputQueueManager.ImmediateQueue.Items).TurnCompletion);
                Assert.False(immediate.Settlement.IsCompleted);
            }
            var restoredState = await freshStore().RestoreAsync(new() { AgentSessionId = productId });
            Assert.Equal(native.SessionId, restoredState!.Value.CopilotSdkSessionId);
            Assert.Empty(await freshStore().ReadMessagesAsync(new() { AgentSessionId = native.SessionId }));
            await using var restored = await AgentChat.CreateAsync(new()
            {
                AgentSessionId = productId, AgentDefinition = null,
                ConfiguredStore = freshStore(), ClientOverride = new CopilotSubAgentChatClient(),
                ForegroundScheduler = foreground,
                AgentServices = new() { RunningAgentChatFactory = factory },
            });
            Assert.Equal(ids, restored.History.Where(item => ids.Contains(item.MessageId)).Select(item => item.MessageId));
            Assert.Equal(3, restored.History.SelectMany(item => item.Contents).OfType<FunctionCallContent>().Count());
            return productId;
        }
        finally
        {
            release.TrySetResult();
            await parent.DisposeAsync().AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            foreach (var lease in factory.CreatedLeases)
                await lease.LocalAgentChat.DisposeAsync();
        }

        void EmitBatch(string id, string text, Action? afterStart = null)
        {
            native.Emit(new ToolExecutionStartEvent { Data = new() { ToolCallId = $"{id}-task", ToolName = "task" } });
            afterStart?.Invoke();
            native.Emit(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = $"{id}-task", Success = true, Result = new() { Content = $"{id} result" } } });
            native.Emit(new AssistantMessageEvent { Data = new() { MessageId = id, Content = text } });
            native.Emit(new SessionIdleEvent { Data = new() });
        }

    }

    private static readonly DateTimeOffset ActiveTime = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static SubagentStartedEvent ActiveStart() => new()
    {
        Id = Guid.NewGuid(), AgentId = "child", Timestamp = ActiveTime,
        Data = new() { ToolCallId = "call", AgentName = "task", AgentDisplayName = "Task", AgentDescription = "test" },
    };
    private static SubagentCompletedEvent ActiveFinish() => new()
    {
        Id = Guid.NewGuid(), AgentId = "child", Timestamp = ActiveTime.AddSeconds(1),
        Data = new() { ToolCallId = "call", AgentName = "task", AgentDisplayName = "Task", Cancelled = false },
    };
    private static async Task ObserveActiveAsync(CopilotSdkChatClient sdk, FakeCopilotSession native, SessionEvent value)
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Processed(Guid id) { if (id == value.Id) observed.TrySetResult(); }
        sdk.StateEventProcessed += Processed;
        try { native.Emit(value); await observed.Task; }
        finally { sdk.StateEventProcessed -= Processed; }
    }
}
