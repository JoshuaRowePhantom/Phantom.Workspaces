using Moq;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests;

public sealed class ReconnectingDevTunnelLifetimeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisposeAsync_PrefersAsyncTransportCleanup_AndWaitsForCompletion(bool persistence, bool asyncOnly)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = Create(persistence,
            _ => Task.FromResult(new DevTunnelEndpointResolution(new Uri("https://selected-8080.euw.devtunnels.ms/"))),
            _ => Task.CompletedTask,
            onDispose: () => throw new InvalidOperationException("Synchronous cleanup must not replace async cleanup."),
            onDisposeAsync: async () =>
            {
                entered.TrySetResult();
                await complete.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }, asyncOnly: asyncOnly);
        await using var owner = fixture.Owner;
        await fixture.Start(TestContext.Current.CancellationToken);
        var disposing = owner.DisposeAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(disposing.IsCompleted);
            complete.TrySetResult();
            await disposing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await owner.DisposeAsync();
            Assert.Equal(1, fixture.Disposals());
        }
        finally
        {
            complete.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisposeAsync_AwaitsAsyncCleanupOfUnpublishedTransport(bool persistence, bool asyncOnly)
    {
        var validating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishValidation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposingTransport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = Create(persistence,
            _ => Task.FromResult(new DevTunnelEndpointResolution(new Uri("https://selected-8080.euw.devtunnels.ms/"))),
            async _ =>
            {
                validating.TrySetResult();
                await finishValidation.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            },
            onDispose: () => throw new InvalidOperationException("Synchronous cleanup must not replace async cleanup."),
            onDisposeAsync: async () =>
            {
                disposingTransport.TrySetResult();
                await finishDisposal.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }, asyncOnly: asyncOnly);
        await using var owner = fixture.Owner;
        var starting = fixture.Start(TestContext.Current.CancellationToken);
        await validating.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var disposing = owner.DisposeAsync().AsTask();
        try
        {
            finishValidation.TrySetResult();
            await disposingTransport.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(starting.IsCompleted);
            Assert.False(disposing.IsCompleted);
            finishDisposal.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
            await disposing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(1, fixture.Disposals());
        }
        finally
        {
            finishValidation.TrySetResult();
            finishDisposal.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconnect_PrefersAsyncCleanupOfReplacedAndCurrentTransports(bool persistence)
    {
        var builds = 0;
        var disposals = 0;
        var delay = new Mock<IDelayScheduler>();
        delay.Setup(value => value.DelayAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Task<DevTunnelEndpointResolution> Resolve(CancellationToken _) =>
            Task.FromResult(new DevTunnelEndpointResolution(new Uri("https://selected-8080.euw.devtunnels.ms/")));
        ValueTask DisposeInner()
        {
            Interlocked.Increment(ref disposals);
            return ValueTask.CompletedTask;
        }
        void RejectSynchronousCleanup() => throw new InvalidOperationException("Async cleanup must be preferred.");

        if (persistence)
        {
            IAgentPersistenceStore Build(DevTunnelEndpointResolution _)
            {
                var inner = new Mock<IAgentPersistenceStore>();
                if (builds++ == 0)
                {
                    inner.Setup(value => value.RestoreAsync(It.IsAny<RestoreRequest>(), It.IsAny<CancellationToken>()))
                        .Returns(() => ValueTask.FromException<PersistedAgent?>(new HttpRequestException("Reconnect.")));
                }
                else
                {
                    inner.Setup(value => value.RestoreAsync(It.IsAny<RestoreRequest>(), It.IsAny<CancellationToken>()))
                        .Returns(ValueTask.FromResult<PersistedAgent?>(null));
                }
                AttachDisposal(inner, RejectSynchronousCleanup, DisposeInner);
                return inner.Object;
            }
            await using var owner = new ReconnectingWebAgentPersistenceStore(Resolve, Build, delay.Object,
                validateConnectionAsync: (_, _) => Task.CompletedTask);
            await owner.StartAsync(TestContext.Current.CancellationToken);
            await owner.RestoreAsync(new RestoreRequest { AgentSessionId = "test" }, TestContext.Current.CancellationToken);
            Assert.Equal(1, disposals);
            await owner.DisposeAsync();
        }
        else
        {
            IDataAccessLayer Build(DevTunnelEndpointResolution _)
            {
                var inner = new Mock<IDataAccessLayer>();
                if (builds++ == 0)
                {
                    inner.Setup(value => value.GetAsync(It.IsAny<GetRequest>(), It.IsAny<CancellationToken>()))
                        .ThrowsAsync(new HttpRequestException("Reconnect."));
                }
                else
                {
                    inner.Setup(value => value.GetAsync(It.IsAny<GetRequest>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new GetResult { Batches = [] });
                }
                AttachDisposal(inner, RejectSynchronousCleanup, DisposeInner);
                return inner.Object;
            }
            await using var owner = new ReconnectingWebDataAccessLayer(Resolve, Build, delay.Object,
                validateConnectionAsync: (_, _) => Task.CompletedTask);
            await owner.StartAsync(TestContext.Current.CancellationToken);
            await owner.GetAsync(new GetRequest { Entities = [] }, TestContext.Current.CancellationToken);
            Assert.Equal(1, disposals);
            await owner.DisposeAsync();
        }
        Assert.Equal(2, builds);
        Assert.Equal(2, disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_WaitsForConcurrentSynchronousTransportCleanup(bool persistence)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = Create(persistence,
            _ => Task.FromResult(new DevTunnelEndpointResolution(new Uri("https://selected-8080.euw.devtunnels.ms/"))),
            _ => Task.CompletedTask, () =>
            {
                entered.TrySetResult();
                complete.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            });
        await using var owner = fixture.Owner;
        await fixture.Start(TestContext.Current.CancellationToken);
        var synchronous = Task.Run(() => ((IDisposable)owner).Dispose(), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            var asynchronous = owner.DisposeAsync().AsTask();
            Assert.False(asynchronous.IsCompleted);
            complete.TrySetResult();
            await Task.WhenAll(synchronous, asynchronous).WaitAsync(TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            Assert.Equal(1, fixture.Disposals());
        }
        finally
        {
            complete.TrySetResult();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_CancelsPendingResolution(bool persistence)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = Create(persistence, async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Cancelled resolution cannot publish an endpoint.");
        }, _ => Task.CompletedTask);
        await using var owner = fixture.Owner;
        var starting = fixture.Start(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.Throws<ObjectDisposedException>(() => { _ = fixture.Start(TestContext.Current.CancellationToken); });
        Assert.Equal(0, fixture.Disposals());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_WaitsForPendingValidation_AndDisposesUnpublishedTransport(bool persistence)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fixture = Create(persistence,
            _ => Task.FromResult(new DevTunnelEndpointResolution(new Uri("https://selected-8080.euw.devtunnels.ms/"))),
            async _ =>
            {
                entered.TrySetResult();
                await complete.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            });
        await using var owner = fixture.Owner;
        var starting = fixture.Start(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var disposing = owner.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        complete.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        await disposing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await owner.DisposeAsync();
        Assert.Equal(1, fixture.Disposals());
        Assert.NotEqual(DevTunnelConnectionState.Connected, fixture.Status().State);
    }

    private static (IAsyncDisposable Owner, Func<CancellationToken, Task> Start,
        Func<int> Disposals, Func<DevTunnelConnectionStatus> Status) Create(
        bool persistence,
        Func<CancellationToken, Task<DevTunnelEndpointResolution>> resolve,
        Func<CancellationToken, Task> validate,
        Action? onDispose = null,
        Func<ValueTask>? onDisposeAsync = null,
        bool asyncOnly = false)
    {
        var disposals = 0;
        void DisposeInner()
        {
            Interlocked.Increment(ref disposals);
            onDispose?.Invoke();
        }
        async ValueTask DisposeInnerAsync()
        {
            Interlocked.Increment(ref disposals);
            await onDisposeAsync!().ConfigureAwait(false);
        }
        if (persistence)
        {
            var inner = new Mock<IAgentPersistenceStore>();
            AttachDisposal(inner, asyncOnly ? null : DisposeInner, onDisposeAsync is null ? null : DisposeInnerAsync);
            var owner = new ReconnectingWebAgentPersistenceStore(resolve, _ => inner.Object,
                new Mock<IDelayScheduler>().Object, validateConnectionAsync: (_, token) => validate(token));
            return (owner, owner.StartAsync, () => disposals, () => owner.Status);
        }
        else
        {
            var inner = new Mock<IDataAccessLayer>();
            AttachDisposal(inner, asyncOnly ? null : DisposeInner, onDisposeAsync is null ? null : DisposeInnerAsync);
            var owner = new ReconnectingWebDataAccessLayer(resolve, _ => inner.Object,
                new Mock<IDelayScheduler>().Object, validateConnectionAsync: (_, token) => validate(token));
            return (owner, owner.StartAsync, () => disposals, () => owner.Status);
        }
    }

    private static void AttachDisposal<T>(Mock<T> inner, Action? synchronous, Func<ValueTask>? asynchronous)
        where T : class
    {
        if (synchronous is not null)
        {
            inner.As<IDisposable>().Setup(value => value.Dispose()).Callback(synchronous);
        }
        if (asynchronous is not null)
        {
            inner.As<IAsyncDisposable>().Setup(value => value.DisposeAsync()).Returns(asynchronous);
        }
    }
}
