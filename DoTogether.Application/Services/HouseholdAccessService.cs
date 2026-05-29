using DoTogether.Application.Exceptions;
using DoTogether.Application.Interfaces;
using DoTogether.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Application.Services;

public class HouseholdAccessService(IAppDbContext db)
{
    /// <summary>
    /// Throws 404 if the household does not exist, 403 if the caller is not a member.
    /// Returns the caller's role within the household.
    /// </summary>
    public async Task<MemberRole> EnsureMemberAsync(Guid householdId, Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
            throw ApiProblemException.Unauthorized("unauthenticated", "Authentication required.");

        var householdExists = await db.Households.AnyAsync(h => h.Id == householdId && !h.IsDeleted, ct);
        if (!householdExists)
            throw ApiProblemException.NotFound("household_not_found", "Household not found.");

        var membership = await db.HouseholdMembers
            .Where(m => m.HouseholdId == householdId && m.UserId == userId && !m.IsDeleted)
            .Select(m => (MemberRole?)m.Role)
            .FirstOrDefaultAsync(ct);

        if (membership is null)
            throw ApiProblemException.Forbidden(
                "household_access_denied",
                "You do not have access to this household.");

        return membership.Value;
    }

    /// <summary>
    /// Throws 403 if the caller is not an Admin of the household.
    /// </summary>
    public async Task EnsureAdminAsync(Guid householdId, Guid userId, CancellationToken ct)
    {
        var role = await EnsureMemberAsync(householdId, userId, ct);
        if (role != MemberRole.Admin)
            throw ApiProblemException.Forbidden(
                "household_admin_required",
                "Only household admins can perform this action.");
    }
}