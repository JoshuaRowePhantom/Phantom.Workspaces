using Phantom.Workspaces.Install;

namespace Phantom.Workspaces.Install.Tests;

public sealed class UpdateCheckSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Poll_FiresOnFirstCall()
    {
        var clock = new ManualClock(Start);
        var scheduler = new UpdateCheckScheduler(clock);
        var fired = 0;
        scheduler.CheckDue += (_, _) => fired++;

        Assert.True(scheduler.Poll());
        Assert.Equal(1, fired);
        Assert.Equal(Start, scheduler.LastCheckUtc);
    }

    [Fact]
    public void Poll_DoesNotFireAgainBeforeInterval()
    {
        var clock = new ManualClock(Start);
        var scheduler = new UpdateCheckScheduler(clock);
        var fired = 0;
        scheduler.CheckDue += (_, _) => fired++;

        Assert.True(scheduler.Poll());
        clock.Advance(TimeSpan.FromHours(5));
        Assert.False(scheduler.Poll());
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Poll_FiresAgainAfterIntervalElapses()
    {
        var clock = new ManualClock(Start);
        var scheduler = new UpdateCheckScheduler(clock);
        var fired = 0;
        scheduler.CheckDue += (_, _) => fired++;

        Assert.True(scheduler.Poll());
        clock.Advance(TimeSpan.FromHours(6));
        Assert.True(scheduler.Poll());
        Assert.Equal(2, fired);
    }

    [Fact]
    public void Poll_FiresRepeatedlyAcrossMultipleIntervals()
    {
        var clock = new ManualClock(Start);
        var scheduler = new UpdateCheckScheduler(clock);
        var fired = 0;
        scheduler.CheckDue += (_, _) => fired++;

        scheduler.Poll();
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromHours(6));
            Assert.True(scheduler.Poll());
        }

        Assert.Equal(4, fired);
    }

    [Fact]
    public void Interval_IsConfigurable()
    {
        var clock = new ManualClock(Start);
        var scheduler = new UpdateCheckScheduler(clock, TimeSpan.FromMinutes(30));

        Assert.True(scheduler.Poll());
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.False(scheduler.Poll());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(scheduler.Poll());
    }

    [Fact]
    public void TimeUntilNextCheck_CountsDownWithinInterval()
    {
        var clock = new ManualClock(Start);
        var scheduler = new UpdateCheckScheduler(clock);

        scheduler.Poll();
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(TimeSpan.FromHours(4), scheduler.TimeUntilNextCheck());
    }

    [Fact]
    public void MarkChecked_ResetsTheInterval()
    {
        var clock = new ManualClock(Start);
        var scheduler = new UpdateCheckScheduler(clock);

        scheduler.Poll();
        clock.Advance(TimeSpan.FromHours(3));
        scheduler.MarkChecked();
        clock.Advance(TimeSpan.FromHours(5));
        Assert.False(scheduler.Poll());
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(scheduler.Poll());
    }

    [Fact]
    public void Constructor_RejectsNonPositiveInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new UpdateCheckScheduler(new ManualClock(Start), TimeSpan.Zero));
    }
}
