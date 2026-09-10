namespace Phantom.Workspaces.Services.AgentSessions;

internal sealed class AgentSessionOwnershipLease : IAsyncDisposable
{
    private static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(5);
    private readonly TimeProvider timeProvider;
    private readonly Func<CancellationToken, ValueTask<DateTimeOffset?>> renewAsync;
    private readonly Func<CancellationToken, ValueTask> fenceAsync;
    private readonly Func<CancellationToken, ValueTask> releaseAsync;
    private readonly CancellationTokenSource cancellation = new();
    private readonly object gate = new();
    private Task? runTask;
    private Task? quiesceTask;
    private DateTimeOffset confirmedExpiry;
    private int fencing;
    private int released;

    internal AgentSessionOwnershipLease(
        TimeProvider timeProvider,
        Func<CancellationToken, ValueTask<DateTimeOffset?>> renewAsync,
        Func<CancellationToken, ValueTask> fenceAsync,
        Func<CancellationToken, ValueTask> releaseAsync)
    {
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.renewAsync = renewAsync ?? throw new ArgumentNullException(nameof(renewAsync));
        this.fenceAsync = fenceAsync ?? throw new ArgumentNullException(nameof(fenceAsync));
        this.releaseAsync = releaseAsync ?? throw new ArgumentNullException(nameof(releaseAsync));
    }

    internal void Start(DateTimeOffset authoritativeExpiry)
    {
        if (authoritativeExpiry <= this.timeProvider.GetUtcNow())
            throw new ArgumentOutOfRangeException(nameof(authoritativeExpiry));
        if (this.runTask is not null)
            throw new InvalidOperationException("Ownership renewal already started.");
        this.confirmedExpiry = authoritativeExpiry;
        this.runTask = this.RunAsync(this.cancellation.Token);
    }

    internal async ValueTask ReleaseAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref this.released, 1) != 0) return;
        await this.QuiesceAsync().ConfigureAwait(false);
        await this.releaseAsync(ct).ConfigureAwait(false);
    }

    internal ValueTask QuiesceAsync()
    {
        lock (this.gate)
            return new ValueTask(this.quiesceTask ??= this.QuiesceCoreAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await this.QuiesceAsync().ConfigureAwait(false);
        this.cancellation.Dispose();
    }

    private async Task QuiesceCoreAsync()
    {
        this.cancellation.Cancel();
        if (Volatile.Read(ref this.fencing) != 0)
            return;
        var running = this.runTask;
        if (running is not null)
        {
            try { await running.ConfigureAwait(false); }
            catch (OperationCanceledException) when (this.cancellation.IsCancellationRequested) { }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var interval = RenewalInterval;
        while (!ct.IsCancellationRequested)
        {
            var now = this.timeProvider.GetUtcNow();
            var safetyDeadline = this.confirmedExpiry - SafetyMargin;
            var untilSafetyDeadline = safetyDeadline - now;
            if (untilSafetyDeadline <= TimeSpan.Zero)
            {
                await this.FenceAsync(ct).ConfigureAwait(false);
                return;
            }

            await this.DelayAsync(
                interval < untilSafetyDeadline ? interval : untilSafetyDeadline,
                ct).ConfigureAwait(false);
            now = this.timeProvider.GetUtcNow();
            if (now >= safetyDeadline)
            {
                await this.FenceAsync(ct).ConfigureAwait(false);
                return;
            }

            DateTimeOffset? renewed = null;
            using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var renewal = this.renewAsync(renewalCancellation.Token).AsTask();
            var deadline = this.DelayAsync(
                safetyDeadline - now, deadlineCancellation.Token);
            try
            {
                if (await Task.WhenAny(renewal, deadline).ConfigureAwait(false) != renewal)
                {
                    renewalCancellation.Cancel();
                    await this.FenceAsync(ct).ConfigureAwait(false);
                    try { await renewal.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested) { }
                    catch { }
                    return;
                }
                deadlineCancellation.Cancel();
                renewed = await renewal.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
            finally
            {
                deadlineCancellation.Cancel();
                try { await deadline.ConfigureAwait(false); }
                catch (OperationCanceledException) when (deadlineCancellation.IsCancellationRequested) { }
            }

            if (renewed is { } expiry && expiry > this.timeProvider.GetUtcNow())
            {
                this.confirmedExpiry = expiry;
                interval = RenewalInterval;
                continue;
            }
            if (this.timeProvider.GetUtcNow() >= this.confirmedExpiry - SafetyMargin)
            {
                await this.FenceAsync(ct).ConfigureAwait(false);
                return;
            }
            interval = RetryInterval;
        }
    }

    private async Task FenceAsync(CancellationToken ct)
    {
        Volatile.Write(ref this.fencing, 1);
        await this.fenceAsync(ct).ConfigureAwait(false);
    }

    private Task DelayAsync(TimeSpan dueTime, CancellationToken ct)
        => Task.Delay(dueTime, this.timeProvider, ct);
}
