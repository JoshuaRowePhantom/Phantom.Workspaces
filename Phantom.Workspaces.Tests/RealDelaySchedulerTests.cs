using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Services.DevTunnel;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class RealDelaySchedulerTests
{
    [Fact]
    public void DelayAsync_SchedulesOnInjectedTimeProvider_DoesNotCompleteWithoutAdvance()
    {
        var timeProvider = new FakeTimeProvider();
        var scheduler = new RealDelayScheduler(timeProvider);

        var task = scheduler.DelayAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.False(task.IsCompleted);
    }

    [Fact]
    public void DelayAsync_AdvanceByDelay_Completes()
    {
        var timeProvider = new FakeTimeProvider();
        var scheduler = new RealDelayScheduler(timeProvider);

        var task = scheduler.DelayAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(task.IsCompleted);

        timeProvider.Advance(TimeSpan.FromSeconds(5));

        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void DelayAsync_AdvanceLessThanDelay_DoesNotComplete()
    {
        var timeProvider = new FakeTimeProvider();
        var scheduler = new RealDelayScheduler(timeProvider);

        var task = scheduler.DelayAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        timeProvider.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromMilliseconds(1));

        Assert.False(task.IsCompleted);
    }

    [Fact]
    public async Task DelayAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        var timeProvider = new FakeTimeProvider();
        var scheduler = new RealDelayScheduler(timeProvider);
        using var cts = new CancellationTokenSource();

        var task = scheduler.DelayAsync(TimeSpan.FromSeconds(5), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void Constructor_WithoutTimeProvider_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new RealDelayScheduler(null!));
    }
}
