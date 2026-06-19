using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// The parsed recurrence model of a <c>schedule</c> entity's <c>repeat</c> block (see
/// <c>docs/design/scheduled-tools.md</c> and <c>JsonSchemas/schedule.json</c>). Times are
/// interpreted in UTC.
/// </summary>
public sealed record ScheduleDefinition
{
    /// <summary>
    /// The interval between runs when <see cref="StartAtTimesOfDay"/> is empty. A zero frequency
    /// with no start-at times means the schedule runs once.
    /// </summary>
    public required TimeSpan Frequency { get; init; }

    /// <summary>
    /// The days of week on which the schedule may run. An empty set means every day.
    /// </summary>
    public required IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; }

    /// <summary>
    /// The UTC times of day at which the schedule runs. When non-empty, the schedule runs at these
    /// times (on the allowed days) rather than on a fixed interval.
    /// </summary>
    public required IReadOnlyList<TimeSpan> StartAtTimesOfDay { get; init; }

    /// <summary>
    /// Parses a <see cref="ScheduleDefinition"/> from a <c>schedule</c> entity's JSON. The entity is
    /// expected to carry a <c>repeat</c> object with <c>frequency</c>, optional <c>days-of-week</c>,
    /// and optional <c>start-at</c> times.
    /// </summary>
    public static ScheduleDefinition FromEntity(JsonElement entity)
    {
        if (entity.ValueKind != JsonValueKind.Object
            || !entity.TryGetProperty("repeat", out var repeat)
            || repeat.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Schedule entity is missing a 'repeat' object.", nameof(entity));
        }

        var frequency = repeat.TryGetProperty("frequency", out var frequencyElement)
            && frequencyElement.ValueKind == JsonValueKind.String
                ? ParseTimeComponent(frequencyElement.GetString()!)
                : TimeSpan.Zero;

        var daysOfWeek = new List<DayOfWeek>();
        if (repeat.TryGetProperty("days-of-week", out var daysElement)
            && daysElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var dayElement in daysElement.EnumerateArray())
            {
                if (dayElement.ValueKind == JsonValueKind.String
                    && TryParseDayOfWeek(dayElement.GetString(), out var day))
                {
                    daysOfWeek.Add(day);
                }
            }
        }

        var startAtTimesOfDay = new List<TimeSpan>();
        if (repeat.TryGetProperty("start-at", out var startAtElement)
            && startAtElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var timeElement in startAtElement.EnumerateArray())
            {
                if (timeElement.ValueKind == JsonValueKind.String)
                {
                    startAtTimesOfDay.Add(ParseTimeComponent(timeElement.GetString()!));
                }
            }
        }

        return new ScheduleDefinition
        {
            Frequency = frequency,
            DaysOfWeek = daysOfWeek,
            StartAtTimesOfDay = startAtTimesOfDay,
        };
    }

    /// <summary>
    /// Parses a schema <c>time</c> string (RFC 3339 full-time, for example <c>09:00:00Z</c>) or a
    /// duration string (for example <c>00:15:00Z</c>) into a <see cref="TimeSpan"/>. A trailing UTC
    /// designator or offset is stripped because schedules are interpreted in UTC.
    /// </summary>
    public static TimeSpan ParseTimeComponent(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = value.Trim();

        if (text.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^1];
        }
        else
        {
            var offsetIndex = text.IndexOf('+');
            if (offsetIndex > 0)
            {
                text = text[..offsetIndex];
            }
        }

        return TimeSpan.Parse(text, CultureInfo.InvariantCulture);
    }

    private static bool TryParseDayOfWeek(string? value, out DayOfWeek day)
    {
        day = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "monday": day = DayOfWeek.Monday; return true;
            case "tuesday": day = DayOfWeek.Tuesday; return true;
            case "wednesday": day = DayOfWeek.Wednesday; return true;
            case "thursday": day = DayOfWeek.Thursday; return true;
            case "friday": day = DayOfWeek.Friday; return true;
            case "saturday": day = DayOfWeek.Saturday; return true;
            case "sunday": day = DayOfWeek.Sunday; return true;
            default: return false;
        }
    }
}
