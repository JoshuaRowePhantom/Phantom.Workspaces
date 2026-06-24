using System;
using System.Text.Json;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ScheduleEvaluatorTests
{
    private static readonly DateTimeOffset Wednesday0930 =
        new(2026, 6, 17, 9, 30, 0, TimeSpan.Zero); // 2026-06-17 is a Wednesday.

    private static ScheduleDefinition Interval(TimeSpan frequency, params DayOfWeek[] days) => new()
    {
        Frequency = frequency,
        DaysOfWeek = days,
        StartAtTimesOfDay = [],
    };

    private static ScheduleDefinition DailyAt(TimeSpan timeOfDay, params DayOfWeek[] days) => new()
    {
        Frequency = TimeSpan.Zero,
        DaysOfWeek = days,
        StartAtTimesOfDay = [timeOfDay],
    };

    [Fact]
    public void Interval_NeverRun_IsDue()
    {
        Assert.True(ScheduleEvaluator.IsDue(Interval(TimeSpan.FromMinutes(15)), lastExecution: null, Wednesday0930));
    }

    [Fact]
    public void Interval_DueWhenElapsedExceedsFrequency()
    {
        var last = Wednesday0930 - TimeSpan.FromMinutes(20);
        Assert.True(ScheduleEvaluator.IsDue(Interval(TimeSpan.FromMinutes(15)), last, Wednesday0930));
    }

    [Fact]
    public void Interval_NotDueWhenElapsedBelowFrequency()
    {
        var last = Wednesday0930 - TimeSpan.FromMinutes(5);
        Assert.False(ScheduleEvaluator.IsDue(Interval(TimeSpan.FromMinutes(15)), last, Wednesday0930));
    }

    [Fact]
    public void Interval_NotDueOnDisallowedDayOfWeek()
    {
        // Wednesday is not in the allowed set, so the interval never fires today.
        var schedule = Interval(TimeSpan.FromMinutes(15), DayOfWeek.Monday, DayOfWeek.Friday);
        Assert.False(ScheduleEvaluator.IsDue(schedule, lastExecution: null, Wednesday0930));
    }

    [Fact]
    public void RunOnce_DueOnlyBeforeFirstRun()
    {
        var runOnce = Interval(TimeSpan.Zero);
        Assert.True(ScheduleEvaluator.IsDue(runOnce, lastExecution: null, Wednesday0930));
        Assert.False(ScheduleEvaluator.IsDue(runOnce, Wednesday0930 - TimeSpan.FromHours(1), Wednesday0930));
    }

    [Fact]
    public void TimeOfDay_DueAfterScheduledTime_WhenLastRunBeforeIt()
    {
        var schedule = DailyAt(TimeSpan.FromHours(9));
        var last = Wednesday0930 - TimeSpan.FromHours(2); // 07:30 today, before 09:00 occurrence.
        Assert.True(ScheduleEvaluator.IsDue(schedule, last, Wednesday0930));
    }

    [Fact]
    public void TimeOfDay_NotDue_WhenAlreadyRanAfterScheduledTime()
    {
        var schedule = DailyAt(TimeSpan.FromHours(9));
        var last = Wednesday0930 - TimeSpan.FromMinutes(15); // 09:15 today, after 09:00 occurrence.
        Assert.False(ScheduleEvaluator.IsDue(schedule, last, Wednesday0930));
    }

    [Fact]
    public void TimeOfDay_BeforeScheduledTime_UsesPreviousDayOccurrence()
    {
        var schedule = DailyAt(TimeSpan.FromHours(9));
        var beforeNineAm = new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero);
        // Last ran yesterday at 09:00, so the most recent occurrence (yesterday 09:00) is covered.
        var lastRanYesterday = new DateTimeOffset(2026, 6, 16, 9, 0, 0, TimeSpan.Zero);
        Assert.False(ScheduleEvaluator.IsDue(schedule, lastRanYesterday, beforeNineAm));

        // But if it has not run since before yesterday's 09:00, it is due.
        var lastRanTwoDaysAgo = new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero);
        Assert.True(ScheduleEvaluator.IsDue(schedule, lastRanTwoDaysAgo, beforeNineAm));
    }

    [Fact]
    public void TimeOfDay_RespectsDaysOfWeek()
    {
        // Only Mondays; Wednesday should not be due even after the scheduled time.
        var schedule = DailyAt(TimeSpan.FromHours(9), DayOfWeek.Monday);
        var last = new DateTimeOffset(2026, 6, 15, 9, 0, 0, TimeSpan.Zero); // Monday 09:00.
        Assert.False(ScheduleEvaluator.IsDue(schedule, last, Wednesday0930));
    }

    [Fact]
    public void FromEntity_ParsesDailyAtNine()
    {
        using var document = JsonDocument.Parse(
            """{ "repeat": { "frequency": "00:00:00Z", "days-of-week": [], "start-at": ["09:00:00Z"] } }""");

        var schedule = ScheduleDefinition.FromEntity(document.RootElement);

        Assert.Equal(TimeSpan.Zero, schedule.Frequency);
        Assert.Empty(schedule.DaysOfWeek);
        Assert.Equal(TimeSpan.FromHours(9), Assert.Single(schedule.StartAtTimesOfDay));
    }

    [Fact]
    public void FromEntity_ParsesEveryFifteenMinutes()
    {
        using var document = JsonDocument.Parse(
            """{ "repeat": { "frequency": "00:15:00Z", "days-of-week": [], "start-at": [] } }""");

        var schedule = ScheduleDefinition.FromEntity(document.RootElement);

        Assert.Equal(TimeSpan.FromMinutes(15), schedule.Frequency);
        Assert.Empty(schedule.StartAtTimesOfDay);
    }

    [Fact]
    public void FromEntity_ParsesDaysOfWeek()
    {
        using var document = JsonDocument.Parse(
            """{ "repeat": { "frequency": "00:00:00Z", "days-of-week": ["monday", "friday"], "start-at": ["09:00:00Z"] } }""");

        var schedule = ScheduleDefinition.FromEntity(document.RootElement);

        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], schedule.DaysOfWeek);
    }

    [Fact]
    public void FromEntity_MissingRepeat_Throws()
    {
        using var document = JsonDocument.Parse("""{ "entity-types": ["entity", "schedule"] }""");
        Assert.Throws<ArgumentException>(() => ScheduleDefinition.FromEntity(document.RootElement));
    }

    [Fact]
    public void ParseTimeComponent_StripsUtcDesignator()
    {
        Assert.Equal(TimeSpan.FromHours(8), ScheduleDefinition.ParseTimeComponent("08:00:00Z"));
        Assert.Equal(new TimeSpan(9, 30, 0), ScheduleDefinition.ParseTimeComponent("09:30:00"));
    }
}
