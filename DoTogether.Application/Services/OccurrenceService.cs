using DoTogether.Application.Interfaces;
using DoTogether.Domain.Entities;
using DoTogether.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Application.Services;

/// <summary>
/// Generates <see cref="ChoreOccurrence"/> rows for a template's next 90 days
/// (rolling window) and back-fills any gaps.
/// </summary>
public class OccurrenceService(IAppDbContext db, IDateTimeProvider clock)
{
    private const int DefaultHorizonDays = 90;

    /// <summary>
    /// Ensure occurrences exist from today through today + horizon for the given template.
    /// </summary>
    public async Task GenerateAsync(ChoreTemplate template, string householdTimeZone, CancellationToken ct = default)
    {
        var today = clock.TodayIn(householdTimeZone);
        var rangeStart = template.GeneratedThroughDate?.AddDays(1) ?? template.StartDate;
        var rangeEnd = today.AddDays(DefaultHorizonDays);

        if (template.EndDate.HasValue && template.EndDate.Value < rangeEnd)
            rangeEnd = template.EndDate.Value;

        if (rangeStart > rangeEnd)
            return;

        var dates = RecurrenceGenerator.Generate(
            template.RecurrenceRule, template.StartDate, rangeStart, rangeEnd);

        if (dates.Count == 0)
        {
            template.GeneratedThroughDate = rangeEnd;
            return;
        }

        // Fetch existing dates to avoid duplicates.
        var existingDates = await db.ChoreOccurrences
            .Where(o => o.ChoreTemplateId == template.Id
                        && o.DueDate >= rangeStart
                        && o.DueDate <= rangeEnd
                        && !o.IsDeleted)
            .Select(o => o.DueDate)
            .ToHashSetAsync(ct);

        foreach (var date in dates)
        {
            if (existingDates.Contains(date))
                continue;

            db.ChoreOccurrences.Add(new ChoreOccurrence
            {
                ChoreTemplateId = template.Id,
                HouseholdId = template.HouseholdId,
                AssigneeId = template.AssigneeId,
                DueDate = date,
                Status = OccurrenceStatus.Pending
            });
        }

        template.GeneratedThroughDate = rangeEnd;
    }

    /// <summary>
    /// Marks pending occurrences whose due date has passed as Missed.
    /// Called lazily before calendar queries.
    /// </summary>
    public async Task MarkMissedAsync(Guid householdId, string householdTimeZone, CancellationToken ct = default)
    {
        var today = clock.TodayIn(householdTimeZone);

        var overdue = await db.ChoreOccurrences
            .Where(o => o.HouseholdId == householdId
                        && o.Status == OccurrenceStatus.Pending
                        && o.DueDate < today
                        && !o.IsDeleted)
            .ToListAsync(ct);

        foreach (var occ in overdue)
        {
            occ.Status = OccurrenceStatus.Missed;
            occ.Version++;
        }
    }
}
