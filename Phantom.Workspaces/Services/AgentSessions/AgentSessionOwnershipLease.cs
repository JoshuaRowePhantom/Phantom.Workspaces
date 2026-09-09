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
    private ITimer? timer;
    private Task? runTask;
    private DateTimeOffset confirmedExpiry;
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
        this.cancellation.Cancel();
        this.timer?.Dispose();
        await this.releaseAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        this.cancellation.Cancel();
        this.timer?.Dispose();
        if (this.runTask is not null)
        {
            try { await this.runTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        this.cancellation.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var interval = RenewalInterval;
        while (!ct.IsCancellationRequested)
        {
            await this.WaitForTimerAsync(interval, ct).ConfigureAwait(false);
            DateTimeOffset? renewed = null;
            try { renewed = await this.renewAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }

            if (renewed is { } expiry && expiry > this.timeProvider.GetUtcNow())
            {
                this.confirmedExpiry = expiry;
                interval = RenewalInterval;
                continue;
            }
            if (this.timeProvider.GetUtcNow() >= this.confirmedExpiry - SafetyMargin)
            {
                await this.fenceAsync(ct).ConfigureAwait(false);
                return;
            }
            interval = RetryInterval;
        }
    }

    private async Task WaitForTimerAsync(TimeSpan dueTime, CancellationToken ct)
    {
        var elapsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        this.timer?.Dispose();
        this.timer = this.timeProvider.CreateTimer(_ => elapsed.TrySetResult(), null, dueTime, Timeout.InfiniteTimeSpan);
        await elapsed.Task.WaitAsync(ct).ConfigureAwait(false);
    }
}
