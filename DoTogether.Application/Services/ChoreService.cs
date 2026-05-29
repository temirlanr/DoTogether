using System.Text.Json;
using DoTogether.Application.DTOs;
using DoTogether.Application.Interfaces;
using DoTogether.Domain.Entities;
using DoTogether.Domain.Enums;
using DoTogether.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Application.Services;

public class ChoreService(IAppDbContext db, IDateTimeProvider clock, OccurrenceService occurrenceService)
{
    // ── Template CRUD ──

    public async Task<ChoreTemplateDto> CreateTemplateAsync(
        Guid householdId, Guid userId, CreateChoreTemplateDto dto, CancellationToken ct)
    {
        var household = await db.Households.FirstAsync(h => h.Id == householdId && !h.IsDeleted, ct);

        var template = new ChoreTemplate
        {
            HouseholdId = householdId,
            Title = dto.Title,
            Description = dto.Description,
            RecurrenceRule = BuildRecurrenceRule(dto.RecurrenceRule),
            AssigneeId = dto.AssigneeId,
            CreatedByUserId = userId,
            StartDate = dto.StartDate,
            EndDate = dto.EndDate
        };

        db.ChoreTemplates.Add(template);
        await occurrenceService.GenerateAsync(template, household.TimeZoneId, ct);
        await db.SaveChangesAsync(ct);

        return MapTemplate(template);
    }

    public async Task<ChoreTemplateDto> UpdateTemplateAsync(
        Guid templateId, Guid householdId, UpdateChoreTemplateDto dto, CancellationToken ct)
    {
        var template = await db.ChoreTemplates
            .Include(t => t.Assignee)
            .FirstAsync(t => t.Id == templateId && t.HouseholdId == householdId && !t.IsDeleted, ct);

        var assigneeChanged = false;

        if (dto.Title is not null) template.Title = dto.Title;
        if (dto.DescriptionSpecified) template.Description = dto.Description;
        if (dto.AssigneeIdSpecified && dto.AssigneeId != template.AssigneeId)
        {
            template.AssigneeId = dto.AssigneeId;
            template.Assignee = dto.AssigneeId.HasValue
                ? await db.Users.FirstAsync(u => u.Id == dto.AssigneeId.Value, ct)
                : null;
            assigneeChanged = true;
        }
        if (dto.EndDateSpecified) template.EndDate = dto.EndDate;
        if (dto.IsActive.HasValue) template.IsActive = dto.IsActive.Value;

        if (dto.RecurrenceRule is not null)
        {
            template.RecurrenceRule = BuildRecurrenceRule(dto.RecurrenceRule);
        }

        if (assigneeChanged)
        {
            var householdTimeZoneId = await db.Households
                .Where(h => h.Id == householdId && !h.IsDeleted)
                .Select(h => h.TimeZoneId)
                .FirstAsync(ct);
            var today = clock.TodayIn(householdTimeZoneId);

            var pendingFutureOccurrences = await db.ChoreOccurrences
                .Where(o => o.ChoreTemplateId == templateId
                            && o.HouseholdId == householdId
                            && o.Status == OccurrenceStatus.Pending
                            && o.DueDate >= today
                            && !o.IsDeleted)
                .ToListAsync(ct);

            foreach (var occurrence in pendingFutureOccurrences)
            {
                occurrence.AssigneeId = template.AssigneeId;
                occurrence.Version++;
            }
        }

        template.UpdatedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return MapTemplate(template);
    }

    public async Task SoftDeleteTemplateAsync(Guid templateId, Guid householdId, CancellationToken ct)
    {
        var template = await db.ChoreTemplates
            .FirstAsync(t => t.Id == templateId && t.HouseholdId == householdId && !t.IsDeleted, ct);

        var occurrencesToDelete = await db.ChoreOccurrences
            .Where(o => o.ChoreTemplateId == templateId
                        && o.HouseholdId == householdId
                        && o.Status != OccurrenceStatus.Completed
                        && !o.IsDeleted)
            .ToListAsync(ct);

        foreach (var occurrence in occurrencesToDelete)
        {
            occurrence.IsDeleted = true;
            occurrence.Version++;
            occurrence.UpdatedAtUtc = clock.UtcNow;
        }

        template.IsDeleted = true;
        template.IsActive = false;
        template.UpdatedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ChoreTemplateDto>> ListTemplatesAsync(Guid householdId, CancellationToken ct)
    {
        var templates = await db.ChoreTemplates
            .Include(t => t.Assignee)
            .Where(t => t.HouseholdId == householdId && !t.IsDeleted)
            .OrderBy(t => t.Title)
            .ToListAsync(ct);

        return templates.Select(MapTemplate).ToList();
    }

    public async Task<ChoreTemplateDto> GetTemplateAsync(Guid templateId, Guid householdId, CancellationToken ct)
    {
        var template = await db.ChoreTemplates
            .Include(t => t.Assignee)
            .FirstAsync(t => t.Id == templateId && t.HouseholdId == householdId && !t.IsDeleted, ct);

        return MapTemplate(template);
    }

    // ── Idempotent Occurrence Mutations ──

    /// <summary>
    /// Executes an idempotent mutation (complete/undo/skip) on an occurrence.
    /// If the clientOperationId already exists, returns the original response.
    /// </summary>
    public async Task<ChoreOccurrenceDto> MutateOccurrenceAsync(
        Guid occurrenceId, Guid householdId, Guid userId,
        ChoreEventType eventType, Guid clientOperationId,
        string? metadata, CancellationToken ct)
    {
        // Check idempotency ledger.
        var existing = await db.IdempotentOperations
            .FirstOrDefaultAsync(o => o.ClientOperationId == clientOperationId, ct);

        if (existing is not null)
            return JsonSerializer.Deserialize<ChoreOccurrenceDto>(existing.ResponseJson)!;

        var occurrence = await db.ChoreOccurrences
            .Include(o => o.Assignee)
            .Include(o => o.Events).ThenInclude(e => e.PerformedByUser)
            .FirstAsync(o => o.Id == occurrenceId && o.HouseholdId == householdId && !o.IsDeleted, ct);
        var templateInfo = await db.ChoreTemplates
            .IgnoreQueryFilters()
            .Where(t => t.Id == occurrence.ChoreTemplateId)
            .Select(t => new { t.Title, t.IsDeleted })
            .FirstAsync(ct);

        // Apply state transition.
        occurrence.Status = eventType switch
        {
            ChoreEventType.Completed => OccurrenceStatus.Completed,
            ChoreEventType.Undone => OccurrenceStatus.Pending,
            ChoreEventType.Skipped => OccurrenceStatus.Skipped,
            _ => occurrence.Status
        };
        if (templateInfo.IsDeleted && occurrence.Status != OccurrenceStatus.Completed)
            occurrence.IsDeleted = true;

        occurrence.Version++;
        occurrence.UpdatedAtUtc = clock.UtcNow;

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);

        var choreEvent = new ChoreEvent
        {
            ChoreOccurrenceId = occurrence.Id,
            EventType = eventType,
            PerformedByUserId = userId,
            PerformedByUser = user,
            OccurredAtUtc = clock.UtcNow,
            ClientOperationId = clientOperationId,
            Metadata = metadata
        };
        db.ChoreEvents.Add(choreEvent);

        var dto = MapOccurrence(occurrence, templateInfo.Title);

        db.IdempotentOperations.Add(new IdempotentOperation
        {
            ClientOperationId = clientOperationId,
            HouseholdId = householdId,
            OperationType = eventType.ToString(),
            ResponseJson = JsonSerializer.Serialize(dto)
        });

        await db.SaveChangesAsync(ct);
        return dto;
    }

    public async Task<ChoreOccurrenceDto> ReassignOccurrenceAsync(
        Guid occurrenceId, Guid householdId, Guid userId,
        Guid newAssigneeId, Guid clientOperationId, CancellationToken ct)
    {
        var existing = await db.IdempotentOperations
            .FirstOrDefaultAsync(o => o.ClientOperationId == clientOperationId, ct);

        if (existing is not null)
            return JsonSerializer.Deserialize<ChoreOccurrenceDto>(existing.ResponseJson)!;

        var occurrence = await db.ChoreOccurrences
            .Include(o => o.Assignee)
            .Include(o => o.Events).ThenInclude(e => e.PerformedByUser)
            .FirstAsync(o => o.Id == occurrenceId && o.HouseholdId == householdId && !o.IsDeleted, ct);
        var templateTitle = await db.ChoreTemplates
            .IgnoreQueryFilters()
            .Where(t => t.Id == occurrence.ChoreTemplateId)
            .Select(t => t.Title)
            .FirstAsync(ct);

        var previousAssigneeId = occurrence.AssigneeId;
        occurrence.AssigneeId = newAssigneeId;
        occurrence.Assignee = await db.Users.FirstAsync(u => u.Id == newAssigneeId, ct);
        occurrence.Version++;
        occurrence.UpdatedAtUtc = clock.UtcNow;

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);

        var meta = JsonSerializer.Serialize(new { previousAssigneeId });
        var choreEvent = new ChoreEvent
        {
            ChoreOccurrenceId = occurrence.Id,
            EventType = ChoreEventType.Reassigned,
            PerformedByUserId = userId,
            PerformedByUser = user,
            OccurredAtUtc = clock.UtcNow,
            ClientOperationId = clientOperationId,
            Metadata = meta
        };
        db.ChoreEvents.Add(choreEvent);

        var dto = MapOccurrence(occurrence, templateTitle);

        db.IdempotentOperations.Add(new IdempotentOperation
        {
            ClientOperationId = clientOperationId,
            HouseholdId = householdId,
            OperationType = "Reassigned",
            ResponseJson = JsonSerializer.Serialize(dto)
        });

        await db.SaveChangesAsync(ct);
        return dto;
    }

    // ── Rolling generation for all active templates in a household ──

    public async Task GenerateOccurrencesForHouseholdAsync(Guid householdId, CancellationToken ct)
    {
        var household = await db.Households.FirstAsync(h => h.Id == householdId && !h.IsDeleted, ct);
        var templates = await db.ChoreTemplates
            .Where(t => t.HouseholdId == householdId && t.IsActive && !t.IsDeleted)
            .ToListAsync(ct);

        foreach (var template in templates)
            await occurrenceService.GenerateAsync(template, household.TimeZoneId, ct);

        await db.SaveChangesAsync(ct);
    }

    // ── Mapping helpers ──

    private static ChoreTemplateDto MapTemplate(ChoreTemplate t) => new(
        t.Id, t.Title, t.Description,
        new RecurrenceRuleDto(t.RecurrenceRule.Type, t.RecurrenceRule.Interval,
            t.RecurrenceRule.DaysOfWeek, t.RecurrenceRule.DayOfMonth),
        t.AssigneeId, t.Assignee?.DisplayName,
        t.StartDate, t.EndDate, t.IsActive, t.CreatedAtUtc);

    private static RecurrenceRule BuildRecurrenceRule(RecurrenceRuleDto dto)
    {
        if (dto.Interval < 1)
            throw new ArgumentException("Recurrence interval must be at least 1.", nameof(dto));

        List<int> daysOfWeek = dto.Type == RecurrenceType.Weekly ? dto.DaysOfWeek ?? [] : [];
        if (dto.Type == RecurrenceType.Weekly)
        {
            if (daysOfWeek.Count == 0)
                throw new ArgumentException("Weekly recurrence requires at least one day of week.", nameof(dto));

            if (daysOfWeek.Any(day => day is < 1 or > 7))
                throw new ArgumentException("Weekly recurrence days must use ISO values 1 through 7.", nameof(dto));
        }

        if (dto.Type == RecurrenceType.Monthly && dto.DayOfMonth is < 1 or > 31)
            throw new ArgumentException("Monthly recurrence day must be between 1 and 31.", nameof(dto));

        return new RecurrenceRule
        {
            Type = dto.Type,
            Interval = dto.Interval,
            DaysOfWeek = daysOfWeek,
            DayOfMonth = dto.Type == RecurrenceType.Monthly ? dto.DayOfMonth : null
        };
    }

    private static ChoreOccurrenceDto MapOccurrence(ChoreOccurrence o, string choreTitle) => new(
        o.Id, o.ChoreTemplateId, choreTitle,
        o.AssigneeId, o.Assignee?.DisplayName, o.DueDate, o.Status, o.Version,
        o.Events.OrderBy(e => e.OccurredAtUtc).Select(e => new ChoreEventDto(
            e.Id, e.EventType, e.PerformedByUserId,
            e.PerformedByUser.DisplayName, e.OccurredAtUtc,
            e.ClientOperationId, e.Metadata)).ToList());
}
