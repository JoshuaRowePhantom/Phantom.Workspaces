using System.Text.Json;
using System.Threading.Channels;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Tests;

public sealed class RemoteAgentSessionRuntimeRegistryTests
{
    [Fact]
    public async Task GetOrStartAsync_ConcurrentCallers_StartsFactoryOnce()
    {
        await using var registry = new RemoteAgentSessionRuntimeRegistry(TimeProvider.System);
        var ready = new TaskCompletionSource<RemoteAgentSessionLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<RemoteAgentSessionLease> Start(CancellationToken _) { Interlocked.Increment(ref calls); return ready.Task; }

        var first = registry.GetOrStartAsync(Intent(), Start, TestContext.Current.CancellationToken).AsTask();
        var second = registry.GetOrStartAsync(Intent(), Start, TestContext.Current.CancellationToken).AsTask();
        var lease = Lease(background: true);
        ready.SetResult(lease);

        Assert.Same(await first, await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrStartAsync_CancelledFactory_RemovesFailedEntry()
    {
        await using var registry = new RemoteAgentSessionRuntimeRegistry(TimeProvider.System);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            registry.GetOrStartAsync(Intent(), _ => Task.FromCanceled<RemoteAgentSessionLease>(new(true)),
                TestContext.Current.CancellationToken).AsTask());
        var lease = Lease(background: true);
        Assert.Same(lease, await registry.GetOrStartAsync(
            Intent(), _ => Task.FromResult(lease), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetOrStartAsync_CancelledWaiter_DoesNotRemoveSharedFactory()
    {
        await using var registry = new RemoteAgentSessionRuntimeRegistry(TimeProvider.System);
        var ready = new TaskCompletionSource<RemoteAgentSessionLease>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<RemoteAgentSessionLease> Start(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            return ready.Task;
        }

        var first = registry.GetOrStartAsync(
            Intent(), Start, TestContext.Current.CancellationToken).AsTask();
        using var cancellation = new CancellationTokenSource();
        var cancelled = registry.GetOrStartAsync(Intent(), Start, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var third = registry.GetOrStartAsync(
            Intent(), Start, TestContext.Current.CancellationToken).AsTask();

        var lease = Lease(background: true);
        ready.SetResult(lease);

        Assert.Same(lease, await first);
        Assert.Same(lease, await third);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DisposeAsync_StartupInFlight_DisposesLeaseAndRejectsWaitingCaller()
    {
        var registry = new RemoteAgentSessionRuntimeRegistry(TimeProvider.System);
        var ready = new TaskCompletionSource<RemoteAgentSessionLease>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = registry.GetOrStartAsync(
            Intent(), _ => ready.Task, TestContext.Current.CancellationToken).AsTask();
        var dispose = registry.DisposeAsync().AsTask();
        var chat = Chat();
        var lease = Lease(background: true, chat: chat.Object);

        ready.SetResult(lease);

        await dispose;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        Assert.True(lease.IsFenced);
        chat.Verify(value => value.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task TryGetAsync_WrongGeneration_ReturnsNull()
    {
        await using var registry = new RemoteAgentSessionRuntimeRegistry(TimeProvider.System);
        var lease = Lease(background: true);
        await registry.GetOrStartAsync(Intent(), _ => Task.FromResult(lease), TestContext.Current.CancellationToken);
        Assert.Null(await registry.TryGetAsync("session", 2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryTerminateAsync_StaleEpoch_ReturnsFalse()
    {
        await using var registry = new RemoteAgentSessionRuntimeRegistry(TimeProvider.System);
        var lease = Lease(background: true);
        await registry.GetOrStartAsync(Intent(), _ => Task.FromResult(lease), TestContext.Current.CancellationToken);
        Assert.False(await registry.TryTerminateAsync(new TerminateAgentSessionRuntimeRequest
        {
            SessionId = "session", OwnershipGeneration = 1, Epoch = Epoch(),
        }, TestContext.Current.CancellationToken));
        Assert.False(lease.IsFenced);
    }

    [Fact]
    public async Task TryTerminateAsync_ExactEpoch_FencesThenDisposesOnce()
    {
        await using var registry = new RemoteAgentSessionRuntimeRegistry(TimeProvider.System);
        var chat = Chat();
        var lease = Lease(background: true, chat: chat.Object);
        await registry.GetOrStartAsync(Intent(), _ => Task.FromResult(lease), TestContext.Current.CancellationToken);
        Assert.True(await registry.TryTerminateAsync(new TerminateAgentSessionRuntimeRequest
        {
            SessionId = "session", OwnershipGeneration = 1, Epoch = lease.Epoch,
        }, TestContext.Current.CancellationToken));
        Assert.True(lease.IsFenced);
        chat.Verify(value => value.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Attach_MultipleViewers_UsesIndependentAttachmentLeases()
    {
        await using var lease = Lease(background: true);
        await using var first = lease.Attach(Attach("a"));
        await using var second = lease.Attach(Attach("b"));
        Assert.Equal(2, lease.ViewerCount);
        await first.DisposeAsync();
        Assert.Equal(1, lease.ViewerCount);
        Assert.False(lease.IsFenced);
    }

    [Fact]
    public async Task AttachmentDispose_NonFinalViewer_DoesNotDisposeRuntime()
    {
        var chat = Chat();
        await using var lease = Lease(background: false, chat: chat.Object);
        var first = lease.Attach(Attach("a"));
        await using var second = lease.Attach(Attach("b"));
        await first.DisposeAsync();
        Assert.Equal(1, lease.ViewerCount);
        Assert.False(lease.IsFenced);
        chat.Verify(value => value.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task AttachmentDispose_LastViewer_DefaultPolicy_DisposesRuntime()
    {
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = Lease(background: false, persistTerminal: _ => { terminal.SetResult(); return ValueTask.CompletedTask; });
        var attachment = lease.Attach(Attach("a"));
        await attachment.DisposeAsync();
        await terminal.Task;
        Assert.True(lease.IsFenced);
    }

    [Fact]
    public async Task AttachmentDispose_LastViewer_BackgroundEnabled_PreservesRuntime()
    {
        await using var lease = Lease(background: true);
        var attachment = lease.Attach(Attach("a"));
        await attachment.DisposeAsync();
        Assert.Equal(0, lease.ViewerCount);
        Assert.False(lease.IsFenced);
    }

    [Fact]
    public async Task TransportLoss_ReconnectWithinFiveSeconds_ReusesAttachmentAndEpoch()
    {
        var time = new FakeTimeProvider();
        await using var lease = Lease(background: false, time: time);
        var first = lease.Attach(Attach("a"));
        await first.MarkTransportLostAsync();
        time.Advance(TimeSpan.FromSeconds(4));
        await using var reconnected = lease.Attach(Attach("a"));
        Assert.Equal(1, lease.ViewerCount);
        Assert.False(lease.IsFenced);
    }

    [Fact]
    public async Task TransportLoss_GraceExpiresAsLastViewer_DefaultPolicy_DisposesRuntimeAndChildren()
    {
        var time = new FakeTimeProvider();
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = Lease(false, time: time, persistTerminal: _ => { terminal.TrySetResult(); return ValueTask.CompletedTask; });
        var attachment = lease.Attach(Attach("a"));
        await attachment.MarkTransportLostAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        await terminal.Task;
        Assert.True(lease.IsFenced);
    }

    [Fact]
    public async Task TransportLoss_GraceExpiresAsLastViewer_BackgroundEnabled_PreservesRuntimeAndChildren()
    {
        var time = new FakeTimeProvider();
        var chat = Chat();
        await using var lease = Lease(true, chat: chat.Object, time: time);
        var attachment = lease.Attach(Attach("a"));
        await attachment.MarkTransportLostAsync();
        var released = attachment.Released;
        time.Advance(TimeSpan.FromSeconds(5));
        await released;
        Assert.Equal(0, lease.ViewerCount);
        Assert.False(lease.IsFenced);
        chat.Verify(value => value.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task SetContinueInBackground_FalseAtZeroViewers_StopsImmediately()
    {
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = Lease(true, persistTerminal: _ => { terminal.SetResult(); return ValueTask.CompletedTask; });
        await lease.SetContinueInBackgroundAsync(false, TestContext.Current.CancellationToken);
        await terminal.Task;
        Assert.True(lease.IsFenced);
    }

    [Fact]
    public async Task SetContinueInBackground_TrueWithViewers_PersistsWithoutStopping()
    {
        var persisted = false;
        await using var lease = Lease(false, persistRetention: (value, _) => { persisted = value; return ValueTask.CompletedTask; });
        await using var attachment = lease.Attach(Attach("a"));
        await lease.SetContinueInBackgroundAsync(true, TestContext.Current.CancellationToken);
        Assert.True(persisted);
        Assert.False(lease.IsFenced);
    }

    [Fact]
    public async Task SetContinueInBackground_FalseWithViewers_PersistsWithoutStopping()
    {
        bool? persisted = null;
        await using var lease = Lease(true, persistRetention: (value, _) =>
        {
            persisted = value;
            return ValueTask.CompletedTask;
        });
        await using var attachment = lease.Attach(Attach("a"));
        await lease.SetContinueInBackgroundAsync(false, TestContext.Current.CancellationToken);
        Assert.False(persisted);
        Assert.False(lease.IsFenced);
        Assert.Equal(1, lease.ViewerCount);
    }

    [Fact]
    public async Task TryTerminateAsync_ConcurrentAttachOrPreferenceChange_TerminateWins()
    {
        var persistStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPersist = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = Lease(false, persistRetention: async (_, ct) =>
        {
            persistStarted.SetResult();
            await allowPersist.Task.WaitAsync(ct);
        });
        await using var attachment = lease.Attach(Attach("a"));
        var preference = lease.SetContinueInBackgroundAsync(true, TestContext.Current.CancellationToken).AsTask();
        await persistStarted.Task;
        var terminate = lease.TryTerminateAsync(TestContext.Current.CancellationToken).AsTask();
        allowPersist.SetResult();
        await preference;
        Assert.True(await terminate);
        Assert.True(lease.IsFenced);
        Assert.Throws<InvalidOperationException>(() => lease.Attach(Attach("late")));
    }

    [Fact]
    public async Task TryTerminateAsync_FencedCleanupRejectsConcurrentAttachAndPreferenceChange()
    {
        var persistenceStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPersistence = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var lease = Lease(true, persistTerminal: async _ =>
        {
            persistenceStarted.SetResult();
            await allowPersistence.Task;
        });

        var terminate = lease.TryTerminateAsync(TestContext.Current.CancellationToken).AsTask();
        await persistenceStarted.Task;

        Assert.True(lease.IsFenced);
        Assert.Throws<InvalidOperationException>(() => lease.Attach(Attach("late")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lease.SetContinueInBackgroundAsync(false, TestContext.Current.CancellationToken).AsTask());

        allowPersistence.SetResult();
        Assert.True(await terminate);
    }

    [Fact]
    public async Task TryTerminateAsync_CancelledWaiter_DoesNotCancelCommittedCleanup()
    {
        var persistenceStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowPersistence = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var chat = Chat();
        await using var lease = Lease(true, chat: chat.Object, persistTerminal: async _ =>
        {
            persistenceStarted.SetResult();
            await allowPersistence.Task;
        });
        lease.Terminated += (_, _) => terminated.SetResult();
        using var cancellation = new CancellationTokenSource();
        var terminate = lease.TryTerminateAsync(cancellation.Token).AsTask();
        await persistenceStarted.Task;

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => terminate);
        allowPersistence.SetResult();
        await terminated.Task;

        Assert.True(lease.HasTerminated);
        chat.Verify(value => value.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task RuntimeDispose_ActiveAttachments_DisposesChildrenPersistsThenEmitsOneTerminal()
    {
        var order = new List<string>();
        var channel = new TestChannel();
        var chat = Chat();
        var childRuntime = new Mock<IAsyncDisposable>();
        childRuntime.Setup(value => value.DisposeAsync())
            .Callback(() => order.Add("child"))
            .Returns(ValueTask.CompletedTask);
        chat.Setup(value => value.DisposeAsync())
            .Callback(() => order.Add("root"))
            .Returns(ValueTask.CompletedTask);
        var runtimeTree = new RuntimeTree(chat.Object, childRuntime.Object);
        await using var lease = new RemoteAgentSessionLease(
            "session", 1, Epoch(), chat.Object, true, () => null!,
            persistTerminalAsync: _ =>
            {
                order.Add("persist");
                return ValueTask.CompletedTask;
            },
            runtimeLifetime: runtimeTree);
        lease.Attach(new AttachRemoteAgentSessionRequest { AttachmentToken = "a", Channel = channel });
        await lease.DisposeAsync();
        var frames = new List<JsonElement>();
        while (channel.Reader.TryRead(out var frame)) frames.Add(frame);
        Assert.Equal(["child", "root", "persist"], order);
        Assert.Single(frames, frame => frame.GetRawText().Contains("session-terminal", StringComparison.Ordinal));
        Assert.True(channel.IsDisposed);
        childRuntime.Verify(value => value.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task RuntimeDispose_ActiveTurn_FencesInterruptsPersistsTerminalThenClosesChannels()
    {
        var order = new List<string>();
        TestChannel? channel = null;
        channel = new TestChannel(() =>
        {
            var sawTerminal = false;
            while (channel!.Reader.TryRead(out var frame))
                sawTerminal |= frame.GetRawText().Contains("session-terminal", StringComparison.Ordinal);
            Assert.True(sawTerminal);
            order.Add("close");
        });
        var chat = Chat();
        chat.SetupGet(value => value.IsBusy).Returns(true);
        chat.Setup(value => value.Interrupt()).Callback(() => order.Add("interrupt"));
        chat.Setup(value => value.DisposeAsync()).Callback(() => order.Add("dispose")).Returns(ValueTask.CompletedTask);
        await using var lease = Lease(true, chat: chat.Object, persistTerminal: _ =>
        {
            order.Add("persist");
            return ValueTask.CompletedTask;
        });
        lease.Attach(new AttachRemoteAgentSessionRequest { AttachmentToken = "active", Channel = channel });
        await lease.DisposeAsync();
        Assert.Equal(["interrupt", "dispose", "persist", "close"], order);
        Assert.True(lease.IsFenced);
    }

    [Fact]
    public async Task RuntimeDispose_CleanupFailures_StillEmitsTerminalBeforeClosingChannel()
    {
        TestChannel? channel = null;
        channel = new TestChannel(() =>
        {
            var sawTerminal = false;
            while (channel!.Reader.TryRead(out var frame))
                sawTerminal |= frame.GetRawText().Contains("session-terminal", StringComparison.Ordinal);
            Assert.True(sawTerminal);
        });
        var runtimeTree = new Mock<IAsyncDisposable>();
        runtimeTree.Setup(value => value.DisposeAsync())
            .ThrowsAsync(new InvalidOperationException("runtime cleanup failed"));
        await using var lease = new RemoteAgentSessionLease(
            "session", 1, Epoch(), Chat().Object, true, () => null!,
            persistTerminalAsync: _ => ValueTask.FromException(
                new InvalidOperationException("persistence failed")),
            runtimeLifetime: runtimeTree.Object);
        lease.Attach(new AttachRemoteAgentSessionRequest
        {
            AttachmentToken = "active",
            Channel = channel,
        });

        await lease.DisposeAsync();

        Assert.True(channel.IsDisposed);
        Assert.True(lease.IsFenced);
    }

    [Fact]
    public async Task RuntimeDispose_InterruptAndChannelFailures_DoNotStrandRemainingCleanup()
    {
        var order = new List<string>();
        var chat = Chat();
        chat.SetupGet(value => value.IsBusy).Returns(true);
        chat.Setup(value => value.Interrupt())
            .Callback(() => order.Add("interrupt"))
            .Throws(new InvalidOperationException("interrupt failed"));
        chat.Setup(value => value.DisposeAsync())
            .Callback(() => order.Add("runtime"))
            .Returns(ValueTask.CompletedTask);
        var failing = new ThrowingChannel(() => order.Add("failing-channel"));
        var remaining = new TestChannel(() => order.Add("remaining-channel"));
        await using var lease = Lease(true, chat: chat.Object, persistTerminal: _ =>
        {
            order.Add("persist");
            return ValueTask.CompletedTask;
        });
        lease.Attach(new AttachRemoteAgentSessionRequest
        {
            AttachmentToken = "failing",
            Channel = failing,
        });
        lease.Attach(new AttachRemoteAgentSessionRequest
        {
            AttachmentToken = "remaining",
            Channel = remaining,
        });

        await lease.DisposeAsync();

        Assert.Equal(
            ["interrupt", "runtime", "persist", "failing-channel", "remaining-channel"],
            order);
        Assert.True(remaining.IsDisposed);
        Assert.True(lease.HasTerminated);
    }

    [Fact]
    public async Task RuntimeDispose_InactiveTurn_DoesNotInterrupt()
    {
        var chat = Chat();
        chat.SetupGet(value => value.IsBusy).Returns(false);
        await using var lease = Lease(true, chat: chat.Object);

        await lease.DisposeAsync();

        chat.Verify(value => value.Interrupt(), Times.Never);
        chat.Verify(value => value.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task RuntimeDispose_InFlightOwnershipRenewal_QuiescesBeforeTerminalPersistence()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
        var renewalStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalExited = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var persisted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var ownership = new AgentSessionOwnershipLease(
            time,
            async ct =>
            {
                renewalStarted.SetResult();
                try
                {
                    await new TaskCompletionSource().Task.WaitAsync(ct);
                    return null;
                }
                finally
                {
                    renewalExited.SetResult();
                }
            },
            _ => ValueTask.CompletedTask,
            _ => ValueTask.CompletedTask);
        ownership.Start(time.GetUtcNow() + TimeSpan.FromSeconds(30));
        await using var lease = new RemoteAgentSessionLease(
            "session", 1, Epoch(), Chat().Object, true, () => null!,
            persistTerminalAsync: _ =>
            {
                Assert.True(renewalExited.Task.IsCompleted);
                persisted.SetResult();
                return ValueTask.CompletedTask;
            },
            timeProvider: time,
            ownershipLease: ownership);
        time.Advance(TimeSpan.FromSeconds(10));
        await renewalStarted.Task;

        await lease.DisposeAsync();

        Assert.True(persisted.Task.IsCompleted);
    }

    private static PersistedAgentSessionRuntimeIntent Intent() => new()
    {
        AgentSessionId = "session",
        OwningProfileEntityId = "11111111-1111-1111-1111-111111111111",
        OwnershipGeneration = 1,
        ExecutorBindings = new ExecutorBindings
        {
            SessionExecutor = JsonDocument.Parse("""{"type":"local"}""").RootElement.Clone(),
        },
    };

    private static RemoteAgentSessionLease Lease(
        bool background,
        IAgentChat? chat = null,
        TimeProvider? time = null,
        Func<bool, CancellationToken, ValueTask>? persistRetention = null,
        Func<CancellationToken, ValueTask>? persistTerminal = null)
        => new("session", 1, Epoch(), chat ?? Chat().Object, background, () => null!,
            persistRetention, persistTerminal, time);

    private static Mock<IAgentChat> Chat()
    {
        var chat = new Mock<IAgentChat>();
        chat.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
        chat.SetupGet(value => value.InputQueues).Returns(Mock.Of<IAgentInputQueues>());
        chat.SetupGet(value => value.RunningItems).Returns(new AgentChatRunningItemCollection());
        chat.SetupGet(value => value.SubAgents).Returns(
            new ReadOnlyObservableCollection<IRunningSubAgent>(new ObservableCollection<IRunningSubAgent>()));
        chat.SetupGet(value => value.Modals).Returns(
            new ReadOnlyObservableCollection<AgentChatModal>(new ObservableCollection<AgentChatModal>()));
        chat.Setup(value => value.GetToolSnapshot()).Returns([]);
        return chat;
    }

    private sealed class RuntimeTree(IAgentChat root, IAsyncDisposable child) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await child.DisposeAsync();
            await root.DisposeAsync();
        }
    }

    private static RuntimeEpoch Epoch() => new() { Value = Guid.NewGuid() };
    private static AttachRemoteAgentSessionRequest Attach(string token) => new()
    {
        AttachmentToken = token,
        Channel = new TestChannel(),
    };

    private sealed class TestChannel(Action? onDispose = null) : IMessageChannel
    {
        private readonly Channel<JsonElement> channel = Channel.CreateUnbounded<JsonElement>();
        public ChannelWriter<JsonElement> Writer => this.channel.Writer;
        public ChannelReader<JsonElement> Reader => this.channel.Reader;
        public bool IsDisposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            this.IsDisposed = true;
            onDispose?.Invoke();
            this.channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingChannel(Action onDispose) : IMessageChannel
    {
        private readonly Channel<JsonElement> channel = Channel.CreateUnbounded<JsonElement>();
        public ChannelWriter<JsonElement> Writer => this.channel.Writer;
        public ChannelReader<JsonElement> Reader => this.channel.Reader;

        public ValueTask DisposeAsync()
        {
            onDispose();
            this.channel.Writer.TryComplete();
            return ValueTask.FromException(new InvalidOperationException("channel cleanup failed"));
        }
    }
}
