using System.Globalization;
using DoTogether.Application.DTOs;
using DoTogether.Application.Interfaces;
using DoTogether.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Application.Services;

/// <summary>
/// Computes achievement metrics from ChoreOccurrence status and ChoreEvent timestamps.
/// <para>
/// <b>Correctness rules:</b>
/// <list type="bullet">
///   <item>A chore counts as "completed" only when the occurrence's current Status == Completed.
///         This inherently handles undo (undo resets Status to Pending).</item>
///   <item>Idempotent retry de-duplication is handled upstream by ChoreService's idempotency ledger;
///         the achievement layer trusts occurrence status as the single source of truth.</item>
///   <item>"Today" and "this week" boundaries use the household's IANA timezone
///         (ISO 8601 Monday-based weeks).</item>
///   <item>Late classification: a completion is "on-time" if the latest Completed event's
///         OccurredAtUtc is ≤ end-of-DueDate in household timezone; otherwise "late".</item>
/// </list>
/// </para>
/// </summary>
public class AchievementService(IAppDbContext db, IDateTimeProvider clock)
{
    // ── Shared data loader ──────────────────────────────────────────────────────
    // All all-time endpoints (streaks, badges) need the same two DB queries.
    // Loading once and sharing the result keeps each endpoint at exactly 2 round-trips.

    private sealed record AllTimeData(
        // All occurrences (any status) — needed for perfect-week calculation.
        IReadOnlyList<(Guid Id, DateOnly DueDate, OccurrenceStatus Status)> Occurrences,
        // Latest Completed event timestamp per completed occurrence.
        IReadOnlyDictionary<Guid, DateTime> LatestCompletionUtc);

    /// <summary>Loads all occurrence rows + their latest completion event in 2 queries.</summary>
    private async Task<AllTimeData> LoadAllTimeDataAsync(
        Guid householdId, Guid? filterUserId, CancellationToken ct)
    {
        var baseQuery = db.ChoreOccurrences
            .Where(o => o.HouseholdId == householdId && !o.IsDeleted);

        if (filterUserId.HasValue)
            baseQuery = baseQuery.Where(o => o.AssigneeId == filterUserId.Value);

        // Query 1: every occurrence (only the 3 columns needed downstream).
        var occs = await baseQuery
            .Select(o => new { o.Id, o.DueDate, o.Status })
            .ToListAsync(ct);

        var completedIds = occs
            .Where(o => o.Status == OccurrenceStatus.Completed)
            .Select(o => o.Id)
            .ToHashSet();

        // Query 2: latest Completed event per completed occurrence.
        Dictionary<Guid, DateTime> latestByOcc = [];
        if (completedIds.Count > 0)
        {
            var events = await db.ChoreEvents
                .Where(e => completedIds.Contains(e.ChoreOccurrenceId)
                            && e.EventType == ChoreEventType.Completed
                            && !e.IsDeleted)
                .Select(e => new { e.ChoreOccurrenceId, e.OccurredAtUtc })
                .ToListAsync(ct);

            latestByOcc = events
                .GroupBy(e => e.ChoreOccurrenceId)
                .ToDictionary(g => g.Key, g => g.Max(e => e.OccurredAtUtc));
        }

        return new AllTimeData(
            occs.Select(o => (o.Id, o.DueDate, o.Status)).ToList(),
            latestByOcc);
    }

    /// <summary>Computes streak numbers purely in memory from pre-loaded data.</summary>
    private static StreaksDto ComputeStreaksFrom(AllTimeData data, TimeZoneInfo tz, DateOnly today)
    {
        var completedDates = data.Occurrences
            .Where(o => o.Status == OccurrenceStatus.Completed)
            .Select(o => o.DueDate)
            .Distinct()
            .OrderByDescending(d => d)
            .ToList();

        var (current, longest) = ComputeStreaks(completedDates, today);

        // On-time streak: days where every completion was before end-of-due-date.
        var onTimeDates = data.Occurrences
            .Where(o => o.Status == OccurrenceStatus.Completed
                        && data.LatestCompletionUtc.TryGetValue(o.Id, out var t)
                        && t <= GetEndOfDayUtc(o.DueDate, tz))
            .Select(o => o.DueDate)
            .Distinct()
            .OrderByDescending(d => d)
            .ToList();

        var (currentOnTime, _) = ComputeStreaks(onTimeDates, today);

        return new StreaksDto(current, longest, currentOnTime);
    }

    // ── Public endpoints ────────────────────────────────────────────────────────

    /// <summary>
    /// Aggregated achievement summary for a date range (by occurrence DueDate).
    /// 2 DB queries: occurrences in range + completion events for late/on-time split.
    /// </summary>
    public async Task<AchievementSummaryDto> GetSummaryAsync(
        Guid householdId, string timeZoneId, DateOnly from, DateOnly to,
        Guid? filterUserId, CancellationToken ct)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        var query = db.ChoreOccurrences
            .Where(o => o.HouseholdId == householdId
                        && o.DueDate >= from && o.DueDate <= to
                        && !o.IsDeleted);

        if (filterUserId.HasValue)
            query = query.Where(o => o.AssigneeId == filterUserId.Value);

        // Query 1: thin projection — grouping done in memory to avoid EF GroupBy limits.
        var occurrences = await query
            .Select(o => new { o.Id, o.DueDate, o.Status, o.ChoreTemplateId })
            .ToListAsync(ct);
        var templateIds = occurrences.Select(o => o.ChoreTemplateId).Distinct().ToList();
        var templateTitles = await db.ChoreTemplates
            .IgnoreQueryFilters()
            .Where(t => templateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title })
            .ToDictionaryAsync(t => t.Id, t => t.Title, ct);

        var totalScheduled = occurrences.Count;
        var completed = occurrences.Where(o => o.Status == OccurrenceStatus.Completed).ToList();
        var totalCompleted = completed.Count;
        var completionRate = totalScheduled > 0 ? Math.Round((double)totalCompleted / totalScheduled, 4) : 0;

        var completedByDay = completed
            .GroupBy(o => o.DueDate)
            .Select(g => new CompletedByDayDto(g.Key, g.Count()))
            .OrderBy(d => d.Date)
            .ToList();

        var topTemplates = completed
            .GroupBy(o => new
            {
                o.ChoreTemplateId,
                Title = templateTitles.GetValueOrDefault(o.ChoreTemplateId) ?? string.Empty
            })
            .Select(g => new TopTemplateDto(g.Key.ChoreTemplateId, g.Key.Title, g.Count()))
            .OrderByDescending(t => t.CompletedCount)
            .Take(5)
            .ToList();

        // Query 2: completion event timestamps for the completed subset only.
        var (onTime, late) = await ClassifyLatenessAsync(
            completed.Select(o => (o.Id, o.DueDate)), tz, ct);

        return new AchievementSummaryDto(
            totalCompleted, totalScheduled, completionRate,
            completedByDay, topTemplates, onTime, late);
    }

    /// <summary>
    /// Today's completed chores (by completion event timestamp in household timezone).
    /// 1 DB query.
    /// </summary>
    public async Task<TodayWinsDto> GetTodayAsync(
        Guid householdId, string timeZoneId, Guid? filterUserId, CancellationToken ct)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var today = clock.TodayIn(timeZoneId);
        var todayStartUtc = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(TimeOnly.MinValue), tz);
        var todayEndUtc = TimeZoneInfo.ConvertTimeToUtc(today.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);

        var eventsQuery = db.ChoreEvents
            .Where(e => e.EventType == ChoreEventType.Completed
                        && e.OccurredAtUtc >= todayStartUtc
                        && e.OccurredAtUtc < todayEndUtc
                        && !e.IsDeleted
                        && e.ChoreOccurrence.HouseholdId == householdId
                        && e.ChoreOccurrence.Status == OccurrenceStatus.Completed
                        && !e.ChoreOccurrence.IsDeleted);

        if (filterUserId.HasValue)
            eventsQuery = eventsQuery.Where(e => e.ChoreOccurrence.AssigneeId == filterUserId.Value);

        var events = await eventsQuery
            .Select(e => new
            {
                e.ChoreOccurrenceId,
                e.OccurredAtUtc,
                e.ChoreOccurrence.DueDate,
                e.ChoreOccurrence.ChoreTemplateId
            })
            .ToListAsync(ct);
        var eventTemplateIds = events.Select(e => e.ChoreTemplateId).Distinct().ToList();
        var eventTemplateTitles = await db.ChoreTemplates
            .IgnoreQueryFilters()
            .Where(t => eventTemplateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title })
            .ToDictionaryAsync(t => t.Id, t => t.Title, ct);

        // De-dupe by occurrence: take the latest event per occurrence.
        var completions = events
            .GroupBy(e => e.ChoreOccurrenceId)
            .Select(g =>
            {
                var latest = g.MaxBy(e => e.OccurredAtUtc)!;
                var dueDateEndUtc = GetEndOfDayUtc(latest.DueDate, tz);
                return new TodayCompletionDto(
                    latest.ChoreOccurrenceId,
                    eventTemplateTitles.GetValueOrDefault(latest.ChoreTemplateId) ?? string.Empty,
                    latest.OccurredAtUtc,
                    latest.DueDate,
                    latest.OccurredAtUtc > dueDateEndUtc);
            })
            .OrderByDescending(c => c.CompletedAtUtc)
            .ToList();

        return new TodayWinsDto(completions.Count, completions);
    }

    /// <summary>
    /// Completion and on-time streaks. 2 DB queries via <see cref="LoadAllTimeDataAsync"/>.
    /// </summary>
    public async Task<StreaksDto> GetStreaksAsync(
        Guid householdId, string timeZoneId, Guid? filterUserId, CancellationToken ct)
    {
        var today = clock.TodayIn(timeZoneId);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var data = await LoadAllTimeDataAsync(householdId, filterUserId, ct);
        return ComputeStreaksFrom(data, tz, today);
    }

    /// <summary>
    /// Badges/milestones. 2 DB queries via <see cref="LoadAllTimeDataAsync"/>;
    /// streaks are computed from the same data without an extra round-trip.
    /// </summary>
    public async Task<BadgesResponseDto> GetBadgesAsync(
        Guid householdId, string timeZoneId, Guid? filterUserId, CancellationToken ct)
    {
        var today = clock.TodayIn(timeZoneId);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

        // All badge + streak logic shares this single data load (2 queries total).
        var data = await LoadAllTimeDataAsync(householdId, filterUserId, ct);
        var streaks = ComputeStreaksFrom(data, tz, today);

        var totalCompleted = data.Occurrences.Count(o => o.Status == OccurrenceStatus.Completed);

        // Perfect weeks: every occurrence in a fully-elapsed ISO week is Completed.
        var currentWeekKey = GetIsoWeekKey(today);
        var perfectWeekCount = data.Occurrences
            .GroupBy(o => GetIsoWeekKey(o.DueDate))
            .Where(g => g.Key < currentWeekKey)
            .Count(g => g.All(o => o.Status == OccurrenceStatus.Completed));

        var earned = new List<BadgeDto>();
        var progress = new List<BadgeProgressDto>();

        EvaluateMilestones(earned, progress, "completions", "Completions",
            "Complete {0} chores", totalCompleted, [10, 25, 50, 100, 250, 500, 1000]);

        EvaluateMilestones(earned, progress, "streak", "Day Streak",
            "Maintain a {0}-day streak", streaks.LongestStreakDays, [3, 7, 14, 30]);

        if (perfectWeekCount > 0)
            earned.Add(new BadgeDto("perfect_week", "Perfect Week",
                $"Completed all scheduled chores in a week ({perfectWeekCount}\u00d7)!"));
        else
            progress.Add(new BadgeProgressDto("perfect_week", "Perfect Week",
                "Complete all scheduled chores in a week", 0, 1));

        return new BadgesResponseDto(earned, progress);
    }

    // ── Private helpers ─────────────────────────────────────────────────────────

    private async Task<(int OnTime, int Late)> ClassifyLatenessAsync(
        IEnumerable<(Guid Id, DateOnly DueDate)> completedOccurrences,
        TimeZoneInfo tz, CancellationToken ct)
    {
        var lookup = completedOccurrences.ToDictionary(o => o.Id, o => o.DueDate);
        if (lookup.Count == 0) return (0, 0);

        var ids = lookup.Keys.ToHashSet();
        var completionEvents = await db.ChoreEvents
            .Where(e => ids.Contains(e.ChoreOccurrenceId)
                        && e.EventType == ChoreEventType.Completed
                        && !e.IsDeleted)
            .Select(e => new { e.ChoreOccurrenceId, e.OccurredAtUtc })
            .ToListAsync(ct);

        var latestByOcc = completionEvents
            .GroupBy(e => e.ChoreOccurrenceId)
            .ToDictionary(g => g.Key, g => g.Max(e => e.OccurredAtUtc));

        int onTime = 0, late = 0;
        foreach (var (id, dueDate) in lookup)
        {
            if (!latestByOcc.TryGetValue(id, out var completedAtUtc))
                continue;

            if (completedAtUtc <= GetEndOfDayUtc(dueDate, tz))
                onTime++;
            else
                late++;
        }

        return (onTime, late);
    }

    internal static DateTime GetEndOfDayUtc(DateOnly date, TimeZoneInfo tz)
    {
        var nextDayMidnight = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return TimeZoneInfo.ConvertTimeToUtc(nextDayMidnight, tz);
    }

    internal static (int Current, int Longest) ComputeStreaks(List<DateOnly> datesDesc, DateOnly today)
    {
        if (datesDesc.Count == 0) return (0, 0);

        int current = 0;
        if (datesDesc[0] == today || datesDesc[0] == today.AddDays(-1))
        {
            current = 1;
            for (int i = 1; i < datesDesc.Count; i++)
            {
                if (datesDesc[i] == datesDesc[i - 1].AddDays(-1))
                    current++;
                else
                    break;
            }
        }

        int longest = 1, streak = 1;
        for (int i = 1; i < datesDesc.Count; i++)
        {
            if (datesDesc[i] == datesDesc[i - 1].AddDays(-1))
            {
                streak++;
                if (streak > longest) longest = streak;
            }
            else
            {
                streak = 1;
            }
        }

        return (current, longest);
    }

    private static int GetIsoWeekKey(DateOnly date)
    {
        var dt = date.ToDateTime(TimeOnly.MinValue);
        return ISOWeek.GetYear(dt) * 100 + ISOWeek.GetWeekOfYear(dt);
    }

    private static void EvaluateMilestones(
        List<BadgeDto> earned, List<BadgeProgressDto> progress,
        string prefix, string titleSuffix, string progressDescTemplate,
        int currentValue, int[] thresholds)
    {
        bool nextProgressAdded = false;
        foreach (var threshold in thresholds)
        {
            if (currentValue >= threshold)
            {
                earned.Add(new BadgeDto(
                    $"{prefix}_{threshold}",
                    $"{threshold} {titleSuffix}",
                    $"Reached {threshold} {titleSuffix.ToLowerInvariant()}!"));
            }
            else if (!nextProgressAdded)
            {
                progress.Add(new BadgeProgressDto(
                    $"{prefix}_{threshold}",
                    $"{threshold} {titleSuffix}",
                    string.Format(progressDescTemplate, threshold),
                    currentValue, threshold));
                nextProgressAdded = true;
            }
        }
    }
}