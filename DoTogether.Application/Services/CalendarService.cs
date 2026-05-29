using DoTogether.Application.DTOs;
using DoTogether.Application.Interfaces;
using DoTogether.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Application.Services;

public class CalendarService(IAppDbContext db, OccurrenceService occurrenceService)
{
    public async Task<List<DayAggregateDto>> GetAggregatesAsync(
        Guid householdId, string timeZoneId, DateOnly from, DateOnly to,
        Guid? assigneeId, CancellationToken ct)
    {
        await occurrenceService.MarkMissedAsync(householdId, timeZoneId, ct);
        await db.SaveChangesAsync(ct);

        var query = db.ChoreOccurrences
            .Where(o => o.HouseholdId == householdId
                        && o.DueDate >= from && o.DueDate <= to
                        && !o.IsDeleted);

        if (assigneeId.HasValue)
            query = query.Where(o => o.AssigneeId == assigneeId.Value);

        var rawData = await query
            .Select(o => new { o.DueDate, o.Status })
            .ToListAsync(ct);

        var aggregates = rawData
            .GroupBy(o => o.DueDate)
            .Select(g => new DayAggregateDto(
                g.Key,
                g.Count(o => o.Status == OccurrenceStatus.Pending),
                g.Count(o => o.Status == OccurrenceStatus.Completed),
                g.Count(o => o.Status == OccurrenceStatus.Missed),
                g.Count(o => o.Status == OccurrenceStatus.Skipped)))
            .OrderBy(a => a.Date)
            .ToList();

        return aggregates;
    }

    public async Task<List<ChoreOccurrenceDto>> GetOccurrencesAsync(
        Guid householdId, string timeZoneId, DateOnly from, DateOnly to,
        Guid? assigneeId, CancellationToken ct)
    {
        await occurrenceService.MarkMissedAsync(householdId, timeZoneId, ct);
        await db.SaveChangesAsync(ct);

        var query = db.ChoreOccurrences
            .Include(o => o.Assignee)
            .Include(o => o.Events).ThenInclude(e => e.PerformedByUser)
            .Where(o => o.HouseholdId == householdId
                        && o.DueDate >= from && o.DueDate <= to
                        && !o.IsDeleted);

        if (assigneeId.HasValue)
            query = query.Where(o => o.AssigneeId == assigneeId.Value);

        var occurrences = await query.OrderBy(o => o.DueDate).ToListAsync(ct);
        var templateIds = occurrences.Select(o => o.ChoreTemplateId).Distinct().ToList();
        var templateTitles = await db.ChoreTemplates
            .IgnoreQueryFilters()
            .Where(t => templateIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title })
            .ToDictionaryAsync(t => t.Id, t => t.Title, ct);

        return occurrences.Select(o => new ChoreOccurrenceDto(
            o.Id,
            o.ChoreTemplateId,
            templateTitles.GetValueOrDefault(o.ChoreTemplateId) ?? string.Empty,
            o.AssigneeId,
            o.Assignee?.DisplayName,
            o.DueDate,
            o.Status,
            o.Version,
            o.Events.OrderBy(e => e.OccurredAtUtc).Select(e => new ChoreEventDto(
                e.Id, e.EventType, e.PerformedByUserId,
                e.PerformedByUser.DisplayName, e.OccurredAtUtc,
                e.ClientOperationId, e.Metadata)).ToList()
        )).ToList();
    }
}
