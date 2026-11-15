using System.Collections.Specialized;
using System.Reflection;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Llm.Core.Tests.Infrastructure;
using Phantom.Workspaces.Llm.Interfaces;

#pragma warning disable GHCP001
namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed partial class CopilotSdkChatClientTests
{
    private sealed class RootFaultCleanupResource(Exception failure) : IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public ValueTask DisposeAsync()
        {
            this.Disposals++;
            return ValueTask.FromException(failure);
        }
    }

    private sealed class RootObservationClock : FakeTimeProvider
    {
        public TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            this.Created.TrySetResult();
            return timer;
        }
    }

    private sealed class RootDispatchFailureLog : ILoggerProvider, ILogger
    {
        public TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null && formatter(state, exception).Contains("Session event dispatch failed", StringComparison.Ordinal))
                this.Failure.TrySetResult(exception);
        }
    }

    [Theory]
    [InlineData("observer")]
    [InlineData("retirement")]
    [InlineData("dispatch")]
    [InlineData("poller")]
    public async Task AgentChat_KnownRootFailureDuringAppend_FencesImmediateBeforeConsumerTeardown(string entry)
    {
        var native = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        native.Sending = count => { if (count == 1) requested.TrySetResult(); };
        var immediateCalls = 0;
        native.SendHandler = (message, _) =>
        {
            if (message.Mode == "immediate") Interlocked.Increment(ref immediateCalls);
            return Task.CompletedTask;
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception original = entry == "dispatch"
            ? new InvalidOperationException("original root dispatch wiring failure")
            : new IOException("original canonical root failure");
        var store = new CopilotSdkRootContinuationTests.SerializedStore
        {
            BeforeWriteAsync = async request =>
            {
                if (request.NewMessages?.Any(message => message.Text == "gated before fault") != true) return;
                entered.TrySetResult();
                await release.Task;
            },
        };
        var factory = new DeferredLifecycleFactory();
        factory.Continue.TrySetResult();
        var clock = new RootObservationClock();
        var nativeClient = new FakeCopilotClient(native);
        using var log = new RootDispatchFailureLog();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(log));
        AgentChat? owner = null;
        CopilotSdkChatClient? sdk = null;
        var foreground = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;
        var chat = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = Guid.NewGuid().ToString(),
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"fault-parent","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
                """),
            ConfiguredStore = store, ForegroundScheduler = foreground,
            ChatClientFactoryOverride = _ =>
            {
                sdk = new("gpt-5", "Copilot", null, loggerFactory, queueManager: owner!.InputQueueManager);
                sdk.StateObservationTimeProvider = clock;
                sdk.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(nativeClient));
                return Task.FromResult<IChatClient>(sdk);
            },
            AgentServices = new() { RunningAgentChatFactory = factory },
        }, constructed => owner = constructed);
        var independentFailure = new InvalidOperationException("independent parent resource cleanup");
        var resource = new RootFaultCleanupResource(independentFailure);
        chat.RegisterOwnedResource(resource);
        var retired = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged += (_, _) =>
        {
            if (chat.RunningItems.Count == 0) retired.TrySetResult(chat.DisposeAsync().AsTask());
        };
        try
        {
            var input = chat.EnqueueUserMessageWithCompletion("initial request");
            await requested.Task;
            var warmup = new AgentInputTurnCompletion();
            chat.InputQueueManager.Enqueue(chat.InputQueueManager.ImmediateQueue,
                [new() { Messages = [new ChatMessage(ChatRole.User, "subscription is live")], TurnCompletion = warmup }]);
            await warmup.Settlement;
            Assert.True(Assert.Single(await sdk!.WaitForSteeringDeliveriesAsync()).Delivered);
            Assert.Equal(1, immediateCalls);
            if (entry is "retirement" or "poller")
            {
                await EmitObservedAsync(sdk!, native, LifecycleStart());
                await factory.Created.Task;
            }
            native.Emit(new AssistantMessageDeltaEvent
            {
                Data = new() { MessageId = "gated", DeltaContent = "gated before fault" },
            });
            native.Emit(new ToolExecutionStartEvent { Data = new() { ToolCallId = "flush", ToolName = "read" } });
            native.Emit(new ToolExecutionCompleteEvent
            {
                Data = new() { ToolCallId = "flush", Success = true, Result = new() { Content = "flush result" } },
            });
            await entered.Task;
            if (entry == "observer")
            {
                native.ListTasksHandler = _ => Task.FromException<IReadOnlyList<GitHub.Copilot.Rpc.TaskInfo>>(original);
                native.Emit(new SessionBackgroundTasksChangedEvent { Data = new() });
                await (Task)typeof(CopilotSdkChatClient).GetField("stateObserverPump", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sdk)!;
            }
            else if (entry == "retirement")
            {
                factory.ReleaseAsync = () => ValueTask.FromException(original);
                await EmitObservedAsync(sdk!, native, LifecycleFinish());
                var tasks = (List<Task>)typeof(CopilotSdkChatClient)
                    .GetField("childRetirementTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sdk)!;
                Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => Assert.Single(tasks)));
            }
            else if (entry == "dispatch")
            {
                factory.CreateFailure = original;
                native.Emit(LifecycleStart());
                Assert.Same(original, await log.Failure.Task);
            }
            else
            {
                await clock.Created.Task;
                nativeClient.LiveSessionFailure = original;
                clock.Advance(TimeSpan.FromSeconds(1));
                await (Task)typeof(CopilotSdkChatClient).GetField("liveSessionPoller", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sdk)!;
            }
            Assert.False(input.Settlement.IsCompleted);
            var pending = new AgentInputTurnCompletion();
            chat.InputQueueManager.Enqueue(chat.InputQueueManager.ImmediateQueue,
                [new() { Messages = [new ChatMessage(ChatRole.User, "do not send into failed root")], TurnCompletion = pending }]);
            Assert.Equal(1, immediateCalls);
            Assert.Same(pending, Assert.Single(chat.InputQueueManager.ImmediateQueue.Items).TurnCompletion);
            Assert.False(pending.Settlement.IsCompleted);
            release.TrySetResult();
            var settlement = await Record.ExceptionAsync(() => input.Settlement);
            Assert.Contains(original, settlement is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [settlement!]);
            var disposal = await retired.Task;
            var cleanupFailure = await Record.ExceptionAsync(() => disposal);
            var errors = cleanupFailure is AggregateException cleanupAggregate
                ? cleanupAggregate.Flatten().InnerExceptions.ToArray() : [cleanupFailure!];
            Assert.Contains(independentFailure, errors);
            Assert.All(errors, error => Assert.True(ReferenceEquals(error, original) || ReferenceEquals(error, independentFailure)));
            if (entry == "retirement") Assert.Contains(original, errors);
            Assert.Equal(1, resource.Disposals);
            Assert.Equal(1, immediateCalls);
            Assert.True(Assert.Single(await sdk!.WaitForSteeringDeliveriesAsync()).Delivered);
            Assert.Same(pending, Assert.Single(chat.InputQueueManager.ImmediateQueue.Items).TurnCompletion);
            Assert.Single(await store.ReadMessagesAsync(new() { AgentSessionId = chat.AgentSessionId }),
                message => message.Text == "gated before fault");
            Assert.Equal(0, native.SubscriberCount);
        }
        finally
        {
            release.TrySetResult();
            native.Emit(new SessionIdleEvent { Data = new() });
            await chat.DisposeAsync().AsTask().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (factory.Created.Task.IsCompletedSuccessfully)
                await (await factory.Created.Task).LocalAgentChat.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalObserverFailure_ReconnectionAndLateOldFailureDoNotPoisonFreshAdmission(bool initializing)
    {
        var native = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
        await using var sdk = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        sdk.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
        sdk.SetSubAgentDependencies(new SubAgentTestFakes.FakeRunningAgentChatFactory(), new SubAgentTestFakes.FakeSubAgentTable());
        var original = new IOException("original observer lifetime");
        if (!initializing) await InvokeEnsureSessionAsync(sdk);
        native.ListTasksHandler = _ => Task.FromException<IReadOnlyList<GitHub.Copilot.Rpc.TaskInfo>>(original);
        if (initializing)
            Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => InvokeEnsureSessionAsync(sdk)));
        else
        {
            native.Emit(new SessionBackgroundTasksChangedEvent { Data = new() });
            await (Task)typeof(CopilotSdkChatClient).GetField("stateObserverPump", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sdk)!;
        }
        var old = (CopilotSdkRootEventQueue)typeof(CopilotSdkChatClient)
            .GetField("rootEventQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sdk)!;
        Assert.True(old.IsSealed);
        native.ListTasksHandler = null;
        await InvokeEnsureSessionAsync(sdk);
        var fresh = (CopilotSdkRootEventQueue)typeof(CopilotSdkChatClient)
            .GetField("rootEventQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sdk)!;
        Assert.NotSame(old, fresh);
        typeof(CopilotSdkChatClient).GetMethod("FailRootObservation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sdk, [old, new InvalidOperationException("late old owner")]);
        Assert.False(fresh.IsSealed);
        native.Sending = _ =>
        {
            native.Emit(new AssistantMessageDeltaEvent { Data = new() { MessageId = "fresh", DeltaContent = "fresh usable output" } });
            native.Emit(new SessionIdleEvent { Data = new() });
        };
        var output = new List<ChatResponseUpdate>();
        await foreach (var update in sdk.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "fresh request")]))
            output.Add(update);
        Assert.Equal("fresh usable output", string.Concat(output.SelectMany(update => update.Contents).OfType<TextContent>().Select(text => text.Text)));
        Assert.Equal(1, native.SendAsyncCallCount);
    }
}
