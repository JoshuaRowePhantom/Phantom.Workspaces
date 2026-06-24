using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

/// <summary>A controllable <see cref="IClock"/> for deterministic, timing-free tests.</summary>
public sealed class ManualClock : IClock
{
    public ManualClock(DateTimeOffset start)
    {
        this.UtcNow = start;
    }

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan amount) => this.UtcNow += amount;
}
