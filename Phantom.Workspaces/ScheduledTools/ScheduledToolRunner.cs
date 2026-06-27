using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ScheduledTools;

/// <summary>
/// Drives a <see cref="ScheduledToolHost"/> on a periodic loop: it runs an immediate evaluation on
/// start and then repeats at a fixed interval, never overlapping its own evaluations. The persisted
/// <c>scheduled-tools-paused</c> gate is enforced inside <see cref="ScheduledToolHost.RunDueToolsAsync"/>,
/// so a paused host simply performs no runs. The loop is cancelled and awaited on disposal so no
/// background runs continue after shutdown (see <c>docs/design/scheduled-tools.md</c>).
/// </summary>
public sealed class ScheduledToolRunner : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task> runOnce;
    private readonly Func<CancellationToken, Task> waitForNextTick;
    private readonly CancellationTokenSource cancellation = new();
    private readonly object startLock = new();
    private Task? loopTask;

    /// <param name="runOnce">Performs a single scheduled-tools evaluation.</param>
    /// <param name="waitForNextTick">Waits until the next poll should occur.</param>
    public ScheduledToolRunner(
        Func<CancellationToken, Task> runOnce,
        Func<CancellationToken, Task> waitForNextTick)
    {
        this.runOnce = runOnce ?? throw new ArgumentNullException(nameof(runOnce));
        this.waitForNextTick = waitForNextTick ?? throw new ArgumentNullException(nameof(waitForNextTick));
    }

    /// <summary>Raised when an evaluation faults; the loop logs the fault and continues.</summary>
    public event EventHandler<Exception>? RunFaulted;

    /// <summary>
    /// Builds a runner that evaluates <paramref name="host"/> for the given host and polls at
    /// <paramref name="pollInterval"/> using <paramref name="timeProvider"/>.
    /// </summary>
    public static ScheduledToolRunner Create(
        ScheduledToolHost host,
        EntityId hostEntityId,
        IReadOnlyList<string> hostNameComponents,
        TimeSpan pollInterval,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(hostNameComponents);
        var resolvedTimeProvider = timeProvider ?? TimeProvider.System;
        return new ScheduledToolRunner(
            cancellationToken => host.RunDueToolsAsync(hostEntityId, hostNameComponents, cancellationToken),
            cancellationToken => Task.Delay(pollInterval, resolvedTimeProvider, cancellationToken));
    }

    /// <summary>Starts the loop. Subsequent calls are no-ops.</summary>
    public void Start()
    {
        lock (this.startLock)
        {
            this.loopTask ??= Task.Run(() => this.RunLoopAsync(this.cancellation.Token));
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        // Immediate run on startup, then repeat at the poll interval.
        await this.RunOnceSafelyAsync(cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await this.waitForNextTick(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await this.RunOnceSafelyAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunOnceSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.runOnce(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown requested.
        }
        catch (Exception exception)
        {
            // A single failed evaluation must not stop the periodic loop; surface and continue.
            this.RunFaulted?.Invoke(this, exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        this.cancellation.Cancel();

        if (this.loopTask is { } task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        this.cancellation.Dispose();
    }
}
