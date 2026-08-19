using DoTogether.Application.Exceptions;
using DoTogether.Application.Interfaces;
using DoTogether.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Application.Services;

public sealed record HouseholdAccessContext(MemberRole Role, string TimeZoneId);

public class HouseholdAccessService(IAppDbContext db)
{
    /// <summary>
    /// Throws 404 if the household does not exist, 403 if the caller is not a member.
    /// Returns the caller's role and the household's timezone.
    /// </summary>
    public async Task<HouseholdAccessContext> EnsureMemberAsync(Guid householdId, Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
            throw ApiProblemException.Unauthorized("unauthenticated", "Authentication required.");

        var timeZoneId = await db.Households
            .Where(h => h.Id == householdId)
            .Select(h => h.TimeZoneId)
            .FirstOrDefaultAsync(ct);
        if (timeZoneId is null)
            throw ApiProblemException.NotFound("household_not_found", "Household not found.");

        var membership = await db.HouseholdMembers
            .Where(m => m.HouseholdId == householdId && m.UserId == userId)
            .Select(m => (MemberRole?)m.Role)
            .FirstOrDefaultAsync(ct);

        if (membership is null)
            throw ApiProblemException.Forbidden(
                "household_access_denied",
                "You do not have access to this household.");

        return new HouseholdAccessContext(membership.Value, timeZoneId);
    }

    /// <summary>
    /// Throws 403 if the caller is not an Admin of the household.
    /// </summary>
    public async Task EnsureAdminAsync(Guid householdId, Guid userId, CancellationToken ct)
    {
        var context = await EnsureMemberAsync(householdId, userId, ct);
        if (context.Role != MemberRole.Admin)
            throw ApiProblemException.Forbidden(
                "household_admin_required",
                "Only household admins can perform this action.");
    }

    /// <summary>Returns the other member's user id, or null for a single-member household.</summary>
    public async Task<Guid?> GetPartnerUserIdAsync(Guid householdId, Guid userId, CancellationToken ct)
    {
        var partnerId = await db.HouseholdMembers
            .Where(m => m.HouseholdId == householdId && m.UserId != userId)
            .Select(m => m.UserId)
            .FirstOrDefaultAsync(ct);

        return partnerId == Guid.Empty ? null : partnerId;
    }
}
