using Moq;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests;

public sealed class ReconnectingDevTunnelLifetimeTests
{
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
        Assert.Throws<ObjectDisposedException>(() => fixture.Start(TestContext.Current.CancellationToken));
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
        Action? onDispose = null)
    {
        var disposals = 0;
        if (persistence)
        {
            var inner = new Mock<IAgentPersistenceStore>();
            inner.As<IDisposable>().Setup(store => store.Dispose()).Callback(() => { disposals++; onDispose?.Invoke(); });
            var owner = new ReconnectingWebAgentPersistenceStore(resolve, _ => inner.Object,
                new Mock<IDelayScheduler>().Object, validateConnectionAsync: (_, token) => validate(token));
            return (owner, owner.StartAsync, () => disposals, () => owner.Status);
        }
        else
        {
            var inner = new Mock<IDataAccessLayer>();
            inner.As<IDisposable>().Setup(layer => layer.Dispose()).Callback(() => { disposals++; onDispose?.Invoke(); });
            var owner = new ReconnectingWebDataAccessLayer(resolve, _ => inner.Object,
                new Mock<IDelayScheduler>().Object, validateConnectionAsync: (_, token) => validate(token));
            return (owner, owner.StartAsync, () => disposals, () => owner.Status);
        }
    }
}
