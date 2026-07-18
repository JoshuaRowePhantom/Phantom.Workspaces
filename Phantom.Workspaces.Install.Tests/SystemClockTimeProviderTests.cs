using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

/// <summary>
/// Verifies the production <see cref="SystemClock"/> reads the current instant from an injected
/// <see cref="TimeProvider"/> rather than the wall clock, so update-check cadence is deterministic.
/// </summary>
public sealed class SystemClockTimeProviderTests
{
    [Fact]
    public void UtcNow_ReturnsInjectedTimeProviderNow()
    {
        var instant = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(instant);

        var clock = new SystemClock(timeProvider);

        Assert.Equal(instant, clock.UtcNow);
    }

    [Fact]
    public void UtcNow_AfterAdvance_ReflectsAdvancedTime()
    {
        var start = new DateTimeOffset(2024, 5, 6, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(start);
        var clock = new SystemClock(timeProvider);

        timeProvider.Advance(TimeSpan.FromMinutes(90));

        Assert.Equal(start.AddMinutes(90), clock.UtcNow);
    }
}
