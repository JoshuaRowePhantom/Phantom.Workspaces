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

/// <summary>Production <see cref="IDelayScheduler"/> backed by <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
public sealed class RealDelayScheduler : IDelayScheduler
{
    /// <summary>A shared instance.</summary>
    public static RealDelayScheduler Instance { get; } = new();

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        => Task.Delay(delay, cancellationToken);
}
