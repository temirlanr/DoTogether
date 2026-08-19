using DoTogether.Application.DTOs;
using DoTogether.Application.Interfaces;
using DoTogether.Application.Services;
using DoTogether.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoTogether.Controllers;

/// <summary>
/// Achievements endpoints for celebrating completed chores.
/// All date/week boundaries use the household's IANA timezone.
/// ISO 8601 weeks (Monday = first day) are used for "perfect week" calculations.
/// </summary>
[ApiController]
[Route("api/households/{householdId:guid}/achievements")]
[Authorize]
public class AchievementsController(
    AchievementService achievementService,
    HouseholdAccessService householdAccess,
    ICurrentUserService currentUser) : ControllerBase
{
    /// <summary>
    /// Aggregated achievement summary for a date range.
    /// Includes completion counts, rate, per-day breakdown, top templates, and late/on-time split.
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(AchievementSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(
        Guid householdId, [FromQuery] AchievementSummaryQueryDto query, CancellationToken ct)
    {
        var (tz, uid) = await ResolveContextAsync(householdId, query.Scope, ct);
        var result = await achievementService.GetSummaryAsync(householdId, tz, query.From, query.To, uid, ct);
        return Ok(result);
    }

    /// <summary>
    /// Today's completed chores (based on completion event timestamp in household timezone).
    /// </summary>
    [HttpGet("today")]
    [ProducesResponseType(typeof(TodayWinsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetToday(
        Guid householdId, [FromQuery] AchievementScopeQueryDto query, CancellationToken ct)
    {
        var (tz, uid) = await ResolveContextAsync(householdId, query.Scope, ct);
        var result = await achievementService.GetTodayAsync(householdId, tz, uid, ct);
        return Ok(result);
    }

    /// <summary>
    /// Completion and on-time streaks (consecutive days with ≥ 1 completion).
    /// </summary>
    [HttpGet("streaks")]
    [ProducesResponseType(typeof(StreaksDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStreaks(
        Guid householdId, [FromQuery] AchievementScopeQueryDto query, CancellationToken ct)
    {
        var (tz, uid) = await ResolveContextAsync(householdId, query.Scope, ct);
        var result = await achievementService.GetStreaksAsync(householdId, tz, uid, ct);
        return Ok(result);
    }

    /// <summary>
    /// Deterministic badges/milestones based on completion totals, streaks, and perfect weeks.
    /// </summary>
    [HttpGet("badges")]
    [ProducesResponseType(typeof(BadgesResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBadges(
        Guid householdId, [FromQuery] AchievementScopeQueryDto query, CancellationToken ct)
    {
        var (tz, uid) = await ResolveContextAsync(householdId, query.Scope, ct);
        var result = await achievementService.GetBadgesAsync(householdId, tz, uid, ct);
        return Ok(result);
    }

    private async Task<(string TimeZoneId, Guid? FilterUserId)> ResolveContextAsync(
        Guid householdId, AchievementScope scope, CancellationToken ct)
    {
        var access = await householdAccess.EnsureMemberAsync(householdId, currentUser.UserId, ct);

        Guid? filterUserId = scope switch
        {
            AchievementScope.Me => currentUser.UserId,
            AchievementScope.Partner => await householdAccess.GetPartnerUserIdAsync(householdId, currentUser.UserId, ct)
                ?? throw new KeyNotFoundException("No partner found in this household."),
            _ => null
        };

        return (access.TimeZoneId, filterUserId);
    }
}
