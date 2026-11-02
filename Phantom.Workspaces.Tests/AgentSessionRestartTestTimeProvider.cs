using Microsoft.Extensions.Time.Testing;

namespace Phantom.Workspaces.Tests;

public sealed partial class DataAccessAgentSessionRuntimeHostFactoryTests
{
    private sealed class RestartTestTimeProvider() : FakeTimeProvider(
        DateTimeOffset.Parse("2026-09-09T00:00:00Z"))
    {
        private readonly object gate = new();
        private readonly Dictionary<ObservedTimer, (TimeSpan Delay, DateTimeOffset Due)> timers = [];
        private readonly List<(TimeSpan Delay, int Count, TaskCompletionSource Ready)> waiters = [];

        internal Task WaitForPendingTimersAsync(TimeSpan delay, int count = 1)
        {
            var now = this.GetUtcNow();
            lock (this.gate)
            {
                if (this.CountPendingTimers(delay, now) >= count)
                    return Task.CompletedTask;
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                this.waiters.Add((delay, count, ready));
                return ready.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ObservedTimer(this, base.CreateTimer(callback, state, dueTime, period));
            this.Scheduled(timer, dueTime);
            return timer;
        }

        private int CountPendingTimers(TimeSpan delay, DateTimeOffset now)
            => this.timers.Values.Count(timer => timer.Delay == delay && timer.Due > now);

        private void Scheduled(ObservedTimer timer, TimeSpan delay)
        {
            var now = this.GetUtcNow();
            lock (this.gate)
            {
                this.timers[timer] = (delay, now + delay);
                foreach (var waiter in this.waiters.ToArray())
                    if (this.CountPendingTimers(waiter.Delay, now) >= waiter.Count)
                    {
                        this.waiters.Remove(waiter);
                        waiter.Ready.TrySetResult();
                    }
            }
        }

        private void Disposed(ObservedTimer timer)
        {
            lock (this.gate)
                this.timers.Remove(timer);
        }

        private sealed class ObservedTimer(RestartTestTimeProvider owner, ITimer timer) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (!timer.Change(dueTime, period))
                    return false;
                owner.Scheduled(this, dueTime);
                return true;
            }

            public void Dispose()
            {
                timer.Dispose();
                owner.Disposed(this);
            }

            public async ValueTask DisposeAsync()
            {
                await timer.DisposeAsync();
                owner.Disposed(this);
            }
        }
    }

    [Fact]
    public async Task RestartClock_DeadlineSignal_ExcludesDisposedAndElapsedTimers()
    {
        var time = new RestartTestTimeProvider();
        var delay = TimeSpan.FromSeconds(15);
        using (time.CreateTimer(_ => { }, null, delay, Timeout.InfiniteTimeSpan)) { }
        using var elapsed = time.CreateTimer(_ => { }, null, delay, Timeout.InfiniteTimeSpan);
        time.Advance(delay);
        var ready = time.WaitForPendingTimersAsync(delay, 2);
        using var first = time.CreateTimer(_ => { }, null, delay, Timeout.InfiniteTimeSpan);
        Assert.False(ready.IsCompleted);
        using var second = time.CreateTimer(_ => { }, null, delay, Timeout.InfiniteTimeSpan);
        await ready;
    }

    [Fact]
    public async Task RestartClock_DeadlineSignal_ObservesChangedTimers()
    {
        var time = new RestartTestTimeProvider();
        var delay = TimeSpan.FromSeconds(15);
        await using var timer = time.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var ready = time.WaitForPendingTimersAsync(delay);
        Assert.False(ready.IsCompleted);
        Assert.True(timer.Change(delay, Timeout.InfiniteTimeSpan));
        await ready;
        await time.WaitForPendingTimersAsync(delay);
    }
}
