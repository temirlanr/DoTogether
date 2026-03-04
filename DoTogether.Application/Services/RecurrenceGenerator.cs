using DoTogether.Domain.Enums;
using DoTogether.Domain.ValueObjects;

namespace DoTogether.Application.Services;

/// <summary>
/// Generates a sequence of DateOnly occurrences from a <see cref="RecurrenceRule"/>.
/// All dates are in the household's local timezone (passed as context by the caller).
///
/// Edge-case handling:
///   • Monthly with DayOfMonth > days-in-month → clamped to last day of month.
///   • Leap year Feb 29 → Feb 28 in non-leap years (via clamping).
///   • DST is irrelevant because we operate on DateOnly, not instants.
/// </summary>
public static class RecurrenceGenerator
{
    /// <summary>
    /// Returns all occurrence dates in [<paramref name="rangeStart"/>, <paramref name="rangeEnd"/>]
    /// for the given rule starting from <paramref name="templateStartDate"/>.
    /// </summary>
    public static IReadOnlyList<DateOnly> Generate(
        RecurrenceRule rule,
        DateOnly templateStartDate,
        DateOnly rangeStart,
        DateOnly rangeEnd)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rangeStart, rangeEnd);

        if (rule.Interval < 1)
            throw new ArgumentException("Interval must be >= 1.", nameof(rule));

        return rule.Type switch
        {
            RecurrenceType.Once => GenerateOnce(templateStartDate, rangeStart, rangeEnd),
            RecurrenceType.Daily => GenerateDaily(rule, templateStartDate, rangeStart, rangeEnd),
            RecurrenceType.Weekly => GenerateWeekly(rule, templateStartDate, rangeStart, rangeEnd),
            RecurrenceType.Monthly => GenerateMonthly(rule, templateStartDate, rangeStart, rangeEnd),
            _ => throw new ArgumentOutOfRangeException(nameof(rule))
        };
    }

    private static IReadOnlyList<DateOnly> GenerateOnce(
        DateOnly start, DateOnly rangeStart, DateOnly rangeEnd)
    {
        return start >= rangeStart && start <= rangeEnd ? [start] : [];
    }

    private static IReadOnlyList<DateOnly> GenerateDaily(
        RecurrenceRule rule, DateOnly start, DateOnly rangeStart, DateOnly rangeEnd)
    {
        var dates = new List<DateOnly>();

        // Fast-forward to rangeStart if possible.
        var current = start;
        if (current < rangeStart)
        {
            int daysBehind = rangeStart.DayNumber - current.DayNumber;
            int fullIntervals = daysBehind / rule.Interval;
            current = current.AddDays(fullIntervals * rule.Interval);
            if (current < rangeStart)
                current = current.AddDays(rule.Interval);
        }

        while (current <= rangeEnd)
        {
            dates.Add(current);
            current = current.AddDays(rule.Interval);
        }

        return dates;
    }

    private static IReadOnlyList<DateOnly> GenerateWeekly(
        RecurrenceRule rule, DateOnly start, DateOnly rangeStart, DateOnly rangeEnd)
    {
        if (rule.DaysOfWeek is not { Count: > 0 })
            throw new ArgumentException("Weekly rule requires at least one day of week.", nameof(rule));

        var isoDays = new HashSet<DayOfWeek>(
            rule.DaysOfWeek.Select(IsoToDayOfWeek));

        var dates = new List<DateOnly>();

        // Find the Monday of the start-date's week.
        var startDow = start.DayOfWeek;
        int daysToMonday = ((int)startDow - 1 + 7) % 7; // Mon=0 offset
        var weekStart = start.AddDays(-daysToMonday);

        // Fast-forward whole week-intervals to rangeStart vicinity.
        if (weekStart.AddDays(6) < rangeStart)
        {
            int weeksBehind = (rangeStart.DayNumber - weekStart.DayNumber) / (7 * rule.Interval);
            weekStart = weekStart.AddDays(weeksBehind * 7 * rule.Interval);
        }

        while (weekStart <= rangeEnd)
        {
            for (int d = 0; d < 7; d++)
            {
                var candidate = weekStart.AddDays(d);
                if (candidate < start || candidate < rangeStart)
                    continue;
                if (candidate > rangeEnd)
                    break;
                if (isoDays.Contains(candidate.DayOfWeek))
                    dates.Add(candidate);
            }

            weekStart = weekStart.AddDays(7 * rule.Interval);
        }

        return dates;
    }

    private static IReadOnlyList<DateOnly> GenerateMonthly(
        RecurrenceRule rule, DateOnly start, DateOnly rangeStart, DateOnly rangeEnd)
    {
        int dayOfMonth = rule.DayOfMonth ?? start.Day;
        var dates = new List<DateOnly>();

        // Start from the template's start month.
        int year = start.Year;
        int month = start.Month;

        // Fast-forward by interval months toward rangeStart.
        while (true)
        {
            var candidate = ClampToMonth(year, month, dayOfMonth);
            if (candidate >= rangeStart)
                break;
            AdvanceMonth(ref year, ref month, rule.Interval);
            // Safety: don't loop forever if rangeStart is very far out.
            if (year > rangeEnd.Year + 1)
                return dates;
        }

        while (true)
        {
            var candidate = ClampToMonth(year, month, dayOfMonth);
            if (candidate > rangeEnd)
                break;
            if (candidate >= rangeStart && candidate >= start)
                dates.Add(candidate);
            AdvanceMonth(ref year, ref month, rule.Interval);
            if (year > rangeEnd.Year + 1)
                break;
        }

        return dates;
    }

    /// <summary>
    /// Clamp day to the actual number of days in the target month.
    /// E.g., day=31 in April → April 30; day=29 in Feb (non-leap) → Feb 28.
    /// </summary>
    internal static DateOnly ClampToMonth(int year, int month, int day)
    {
        int maxDay = DateTime.DaysInMonth(year, month);
        return new DateOnly(year, month, Math.Min(day, maxDay));
    }

    private static void AdvanceMonth(ref int year, ref int month, int interval)
    {
        month += interval;
        while (month > 12)
        {
            month -= 12;
            year++;
        }
    }

    private static DayOfWeek IsoToDayOfWeek(int iso) => iso switch
    {
        1 => DayOfWeek.Monday,
        2 => DayOfWeek.Tuesday,
        3 => DayOfWeek.Wednesday,
        4 => DayOfWeek.Thursday,
        5 => DayOfWeek.Friday,
        6 => DayOfWeek.Saturday,
        7 => DayOfWeek.Sunday,
        _ => throw new ArgumentOutOfRangeException(nameof(iso), $"ISO day must be 1–7, got {iso}.")
    };
}
