using System;
using System.Collections.Generic;
using System.Linq;

namespace Phantom.Workspaces.ScheduledTools;

/// <summary>
/// Decides whether a <see cref="ScheduleDefinition"/> is due to run, given the last execution time
/// and the current time. All comparisons are performed in UTC.
/// </summary>
public static class ScheduleEvaluator
{
    /// <summary>
    /// Returns <see langword="true"/> if the schedule should run now.
    /// </summary>
    /// <param name="schedule">The parsed recurrence definition.</param>
    /// <param name="lastExecution">
    /// The time the schedule last ran, or <see langword="null"/> if it has never run.
    /// </param>
    /// <param name="now">The current time.</param>
    public static bool IsDue(
        ScheduleDefinition schedule,
        DateTimeOffset? lastExecution,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var nowUtc = now.ToUniversalTime();
        var lastUtc = lastExecution?.ToUniversalTime();

        return schedule.StartAtTimesOfDay.Count > 0
            ? IsTimeOfDayDue(schedule, lastUtc, nowUtc)
            : IsIntervalDue(schedule, lastUtc, nowUtc);
    }

    private static bool IsIntervalDue(
        ScheduleDefinition schedule,
        DateTimeOffset? lastExecution,
        DateTimeOffset now)
    {
        if (!IsDayAllowed(schedule, now.DayOfWeek))
        {
            return false;
        }

        // No frequency and no start-at times means the schedule runs a single time.
        if (schedule.Frequency <= TimeSpan.Zero)
        {
            return lastExecution is null;
        }

        if (lastExecution is null)
        {
            return true;
        }

        return now - lastExecution.Value >= schedule.Frequency;
    }

    private static bool IsTimeOfDayDue(
        ScheduleDefinition schedule,
        DateTimeOffset? lastExecution,
        DateTimeOffset now)
    {
        var occurrence = MostRecentOccurrenceAtOrBefore(schedule, now);
        if (occurrence is null)
        {
            return false;
        }

        return lastExecution is null || occurrence.Value > lastExecution.Value;
    }

    private static DateTimeOffset? MostRecentOccurrenceAtOrBefore(
        ScheduleDefinition schedule,
        DateTimeOffset now)
    {
        DateTimeOffset? best = null;
        var today = now.UtcDateTime.Date;

        // Look back up to a full week so that day-of-week gaps still resolve a recent occurrence.
        for (var dayOffset = 0; dayOffset <= 7; dayOffset++)
        {
            var day = today.AddDays(-dayOffset);
            if (!IsDayAllowed(schedule, day.DayOfWeek))
            {
                continue;
            }

            foreach (var timeOfDay in schedule.StartAtTimesOfDay)
            {
                var candidate = new DateTimeOffset(day + timeOfDay, TimeSpan.Zero);
                if (candidate <= now && (best is null || candidate > best.Value))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private static bool IsDayAllowed(ScheduleDefinition schedule, DayOfWeek day)
    {
        return schedule.DaysOfWeek.Count == 0 || schedule.DaysOfWeek.Contains(day);
    }
}
