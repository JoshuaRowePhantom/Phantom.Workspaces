namespace Phantom.Workspaces.Install;

/// <summary>
/// A clock seam so update-check cadence is testable by advancing virtual time rather than
/// waiting on the wall clock (matches the deterministic-tests convention).
/// </summary>
public interface IClock
{
    /// <summary>The current UTC instant.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>The production <see cref="IClock"/> backed by an injected <see cref="TimeProvider"/>.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>A shared instance backed by the wall-clock <see cref="TimeProvider.System"/>.</summary>
    public static readonly SystemClock Instance = new();

    private readonly TimeProvider timeProvider;

    /// <summary>Creates a clock that reads the current instant from <paramref name="timeProvider"/>.</summary>
    public SystemClock(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow => this.timeProvider.GetUtcNow();
}
