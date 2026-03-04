using DoTogether.Application.DTOs;
using DoTogether.Application.Interfaces;
using DoTogether.Domain.Entities;
using DoTogether.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Application.Services;

public class HouseholdService(IAppDbContext db, IDateTimeProvider clock)
{
    public async Task<HouseholdDto> CreateAsync(Guid userId, CreateHouseholdDto dto, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct))
            throw new UnauthorizedAccessException("Authenticated user not found.");

        // Validate timezone.
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(dto.TimeZoneId, out _))
            throw new ArgumentException($"Invalid timezone: {dto.TimeZoneId}");

        var household = new Household
        {
            Name = dto.Name,
            TimeZoneId = dto.TimeZoneId
        };

        var member = new HouseholdMember
        {
            HouseholdId = household.Id,
            UserId = userId,
            Role = MemberRole.Admin,
            JoinedAtUtc = clock.UtcNow
        };

        household.Members.Add(member);
        db.Households.Add(household);
        await db.SaveChangesAsync(ct);

        return await GetByIdAsync(household.Id, ct);
    }

    public async Task<HouseholdDto> GetByIdAsync(Guid householdId, CancellationToken ct)
    {
        var h = await db.Households
            .Include(x => x.Members).ThenInclude(m => m.User)
            .FirstAsync(x => x.Id == householdId && !x.IsDeleted, ct);

        return MapHousehold(h);
    }

    public async Task<List<HouseholdDto>> GetForUserAsync(Guid userId, CancellationToken ct)
    {
        var memberOf = await db.HouseholdMembers
            .Include(m => m.Household).ThenInclude(h => h.Members).ThenInclude(m => m.User)
            .Where(m => m.UserId == userId && !m.IsDeleted && !m.Household.IsDeleted)
            .Select(m => m.Household)
            .ToListAsync(ct);

        return memberOf.Select(MapHousehold).ToList();
    }

    public async Task<InviteResponseDto> InviteMemberAsync(
        Guid householdId, Guid inviterId, InviteMemberDto dto, CancellationToken ct)
    {
        // Verify inviter is admin.
        var inviterMember = await db.HouseholdMembers
            .FirstAsync(m => m.HouseholdId == householdId && m.UserId == inviterId && !m.IsDeleted, ct);

        if (inviterMember.Role != MemberRole.Admin)
            throw new UnauthorizedAccessException("Only admins can invite members.");

        // Check household size limit (2-person household).
        var memberCount = await db.HouseholdMembers
            .CountAsync(m => m.HouseholdId == householdId && !m.IsDeleted, ct);

        if (memberCount >= 2)
            throw new InvalidOperationException("Household already has 2 members.");

        var invite = new HouseholdInvite
        {
            HouseholdId = householdId,
            InviteeEmail = dto.Email.ToLowerInvariant(),
            ExpiresAtUtc = clock.UtcNow.AddDays(7)
        };

        db.HouseholdInvites.Add(invite);
        await db.SaveChangesAsync(ct);

        return new InviteResponseDto(invite.Id, invite.Token, invite.ExpiresAtUtc);
    }

    public async Task<HouseholdDto> JoinAsync(Guid userId, JoinHouseholdDto dto, CancellationToken ct)
    {
        var invite = await db.HouseholdInvites
            .FirstAsync(i => i.Token == dto.InviteToken && !i.Accepted && !i.IsDeleted, ct);

        if (invite.ExpiresAtUtc < clock.UtcNow)
            throw new InvalidOperationException("Invite has expired.");

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct);
        if (!string.Equals(user.Email, invite.InviteeEmail, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Invite was sent to a different email address.");

        var memberCount = await db.HouseholdMembers
            .CountAsync(m => m.HouseholdId == invite.HouseholdId && !m.IsDeleted, ct);

        if (memberCount >= 2)
            throw new InvalidOperationException("Household already has 2 members.");

        invite.Accepted = true;

        var member = new HouseholdMember
        {
            HouseholdId = invite.HouseholdId,
            UserId = userId,
            Role = MemberRole.Member,
            JoinedAtUtc = clock.UtcNow
        };

        db.HouseholdMembers.Add(member);
        await db.SaveChangesAsync(ct);

        return await GetByIdAsync(invite.HouseholdId, ct);
    }

    private static HouseholdDto MapHousehold(Household h) => new(
        h.Id, h.Name, h.TimeZoneId,
        h.Members.Where(m => !m.IsDeleted).Select(m => new HouseholdMemberDto(
            m.UserId, m.User.DisplayName, m.User.Email, m.Role, m.JoinedAtUtc)).ToList());
}
