using DoTogether.Domain.Enums;

namespace DoTogether.Domain.ValueObjects;

/// <summary>
/// Represents a recurrence rule. Stored as an owned JSON column in EF Core.
/// 
/// Monthly edge-case policy:
///   If DayOfMonth > days in a given month, clamp to the last day of that month.
///   Example: DayOfMonth=31 → Jan 31, Feb 28/29, Mar 31, Apr 30, …
///
/// Leap year policy:
///   Feb 29 templates produce an occurrence on Feb 28 in non-leap years.
///
/// DST policy:
///   Due dates are DateOnly values in the household timezone. Because we never
///   convert a DateOnly to an instant for scheduling purposes (only for
///   "is it past end-of-day?"), DST transitions do not create ambiguous or
///   skipped occurrences.
/// </summary>
public class RecurrenceRule
{
    public RecurrenceType Type { get; set; } = RecurrenceType.Once;

    /// <summary>Every N days/weeks/months. Ignored for Once.</summary>
    public int Interval { get; set; } = 1;

    /// <summary>For Weekly: which ISO days (1=Mon … 7=Sun). Ignored otherwise.</summary>
    public List<int> DaysOfWeek { get; set; } = [];

    /// <summary>For Monthly: day of month (1–31). Clamped to last day if needed.</summary>
    public int? DayOfMonth { get; set; }
}
