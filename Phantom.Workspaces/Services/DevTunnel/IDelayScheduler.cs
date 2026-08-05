using System;
using System.Threading;
using System.Threading.Tasks;

namespace Phantom.Workspaces.Services.DevTunnel;

/// <summary>
/// Schedules asynchronous delays. Abstracted so reconnect backoff can be driven by an injected,
/// deterministic scheduler in tests (no real timers / <c>Task.Delay</c>).
/// </summary>
public interface IDelayScheduler
{
    /// <summary>Completes after the given delay (or immediately, in a deterministic test scheduler).</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}

/// <summary>Production <see cref="IDelayScheduler"/> backed by an injected <see cref="TimeProvider"/>.</summary>
public sealed class RealDelayScheduler : IDelayScheduler
{
    private readonly TimeProvider timeProvider;

    /// <summary>Creates a scheduler that schedules delays on <paramref name="timeProvider"/>.</summary>
    public RealDelayScheduler(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.timeProvider = timeProvider;
    }

    /// <summary>A shared instance backed by the wall-clock <see cref="TimeProvider.System"/>.</summary>
    public static RealDelayScheduler Instance { get; } = new(TimeProvider.System);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        => Task.Delay(delay, this.timeProvider, cancellationToken);
}
