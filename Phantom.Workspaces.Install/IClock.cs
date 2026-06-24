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

/// <summary>The production <see cref="IClock"/> backed by the system clock.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>A shared instance.</summary>
    public static readonly SystemClock Instance = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
