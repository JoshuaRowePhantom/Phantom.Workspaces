namespace Phantom.Workspaces.Install;

/// <summary>
/// Decides when a periodic update check is due based on an injected <see cref="IClock"/>. The
/// decision is purely clock-based (not timer-based) so production can drive it from any cadence
/// of <see cref="Poll"/> calls and tests can advance virtual time deterministically. The check
/// interval defaults to six hours.
/// </summary>
public sealed class UpdateCheckScheduler
{
    /// <summary>The default cadence between periodic update checks.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);

    private readonly IClock clock;
    private readonly object gate = new();
    private DateTimeOffset? lastCheckUtc;

    /// <summary>Creates a scheduler over <paramref name="clock"/> with an optional interval.</summary>
    public UpdateCheckScheduler(IClock clock, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (interval is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), value, "Interval must be positive.");
        }

        this.clock = clock;
        this.Interval = interval ?? DefaultInterval;
    }

    /// <summary>Raised when <see cref="Poll"/> determines a check is due.</summary>
    public event EventHandler? CheckDue;

    /// <summary>The cadence between checks.</summary>
    public TimeSpan Interval { get; }

    /// <summary>The last instant a check was recorded, or <c>null</c> when none has run.</summary>
    public DateTimeOffset? LastCheckUtc
    {
        get
        {
            lock (this.gate)
            {
                return this.lastCheckUtc;
            }
        }
    }

    /// <summary>
    /// Evaluates whether a check is due at the current clock time. The first poll always fires;
    /// thereafter a check is due once <see cref="Interval"/> has elapsed since the last check.
    /// When due it records the time, raises <see cref="CheckDue"/>, and returns <c>true</c>.
    /// </summary>
    public bool Poll()
    {
        bool due;
        lock (this.gate)
        {
            var now = this.clock.UtcNow;
            due = this.lastCheckUtc is null || now - this.lastCheckUtc.Value >= this.Interval;
            if (due)
            {
                this.lastCheckUtc = now;
            }
        }

        if (due)
        {
            this.CheckDue?.Invoke(this, EventArgs.Empty);
        }

        return due;
    }

    /// <summary>Records that a check ran at the current clock time, resetting the interval.</summary>
    public void MarkChecked()
    {
        lock (this.gate)
        {
            this.lastCheckUtc = this.clock.UtcNow;
        }
    }

    /// <summary>
    /// The remaining time until the next check is due, or <see cref="TimeSpan.Zero"/> when a
    /// check is already due.
    /// </summary>
    public TimeSpan TimeUntilNextCheck()
    {
        lock (this.gate)
        {
            if (this.lastCheckUtc is null)
            {
                return TimeSpan.Zero;
            }

            var elapsed = this.clock.UtcNow - this.lastCheckUtc.Value;
            var remaining = this.Interval - elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }
}
