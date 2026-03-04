using DoTogether.Application.DTOs;
using DoTogether.Application.Interfaces;
using DoTogether.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoTogether.Controllers;

[ApiController]
[Route("api/households/{householdId:guid}/calendar")]
[Authorize]
public class CalendarController(
    CalendarService calendarService,
    IAppDbContext db) : ControllerBase
{
    /// <summary>
    /// Get per-day aggregates (due/done/missed/skipped) for a date range.
    /// </summary>
    [HttpGet("aggregates")]
    [ProducesResponseType(typeof(List<DayAggregateDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAggregates(
        Guid householdId, [FromQuery] CalendarQueryDto query, CancellationToken ct)
    {
        var household = await db.Households.FindAsync([householdId], ct)
            ?? throw new KeyNotFoundException("Household not found.");

        var result = await calendarService.GetAggregatesAsync(
            householdId, household.TimeZoneId, query.From, query.To, query.AssigneeId, ct);
        return Ok(result);
    }

    /// <summary>
    /// Get individual occurrences for a date range (with events/audit trail).
    /// </summary>
    [HttpGet("occurrences")]
    [ProducesResponseType(typeof(List<ChoreOccurrenceDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOccurrences(
        Guid householdId, [FromQuery] CalendarQueryDto query, CancellationToken ct)
    {
        var household = await db.Households.FindAsync([householdId], ct)
            ?? throw new KeyNotFoundException("Household not found.");

        var result = await calendarService.GetOccurrencesAsync(
            householdId, household.TimeZoneId, query.From, query.To, query.AssigneeId, ct);
        return Ok(result);
    }
}
