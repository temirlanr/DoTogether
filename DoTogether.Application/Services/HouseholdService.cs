using DoTogether.Application.DTOs;
using DoTogether.Application.Exceptions;
using DoTogether.Application.Interfaces;
using DoTogether.Domain.Entities;
using DoTogether.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Application.Services;

public class HouseholdService(IAppDbContext db, IDateTimeProvider clock)
{
    private static readonly TimeSpan InviteTokenLifetime = TimeSpan.FromDays(3650);

    public async Task<HouseholdDto> UpdateAsync(Guid householdId, Guid actorUserId, UpdateHouseholdDto dto, CancellationToken ct)
    {
        await EnsureAdminMemberAsync(householdId, actorUserId, ct);

        var household = await db.Households
            .FirstOrDefaultAsync(h => h.Id == householdId && !h.IsDeleted, ct)
            ?? throw ApiProblemException.NotFound("household_not_found", "Household not found.");

        household.Name = dto.Name.Trim();
        household.UpdatedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        return await GetByIdAsync(householdId, ct);
    }

    public async Task<HouseholdDto> CreateAsync(Guid userId, CreateHouseholdDto dto, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct))
            throw ApiProblemException.Unauthorized("unauthenticated", "Authenticated user not found.");

        // Validate timezone.
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(dto.TimeZoneId, out _))
            throw ApiProblemException.BadRequest("invalid_timezone", $"Invalid timezone: {dto.TimeZoneId}");

        var household = new Household
        {
            Name = dto.Name,
            TimeZoneId = dto.TimeZoneId
        };

        var now = clock.UtcNow;

        var member = new HouseholdMember
        {
            HouseholdId = household.Id,
            UserId = userId,
            Role = MemberRole.Admin,
            JoinedAtUtc = now
        };

        household.Members.Add(member);
        household.Invites.Add(CreateInvite(household.Id, now));
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

    public async Task<InviteResponseDto> GetInviteTokenAsync(Guid householdId, Guid requesterId, CancellationToken ct)
    {
        await EnsureMemberAsync(householdId, requesterId, ct);
        await EnsureHouseholdCanAcceptInviteAsync(householdId, ct);

        var now = clock.UtcNow;
        var invite = await db.HouseholdInvites
            .Where(i => i.HouseholdId == householdId && !i.Accepted && i.ExpiresAtUtc > now && !i.IsDeleted)
            .OrderByDescending(i => i.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        if (invite is null)
        {
            invite = CreateInvite(householdId, now);
            db.HouseholdInvites.Add(invite);
            await db.SaveChangesAsync(ct);
        }

        return MapInvite(invite);
    }

    public async Task<InviteResponseDto> RegenerateInviteTokenAsync(Guid householdId, Guid requesterId, CancellationToken ct)
    {
        await EnsureMemberAsync(householdId, requesterId, ct);
        await EnsureHouseholdCanAcceptInviteAsync(householdId, ct);

        var now = clock.UtcNow;
        var existingInvites = await db.HouseholdInvites
            .Where(i => i.HouseholdId == householdId && !i.Accepted && !i.IsDeleted)
            .ToListAsync(ct);

        foreach (var existingInvite in existingInvites)
        {
            existingInvite.IsDeleted = true;
            existingInvite.UpdatedAtUtc = now;
        }

        var invite = CreateInvite(householdId, now);

        db.HouseholdInvites.Add(invite);
        await db.SaveChangesAsync(ct);

        return MapInvite(invite);
    }

    public async Task<HouseholdDto> JoinAsync(Guid userId, JoinHouseholdDto dto, CancellationToken ct)
    {
        var invite = await db.HouseholdInvites
            .FirstOrDefaultAsync(i => i.Token == dto.InviteToken && !i.Accepted && !i.IsDeleted, ct)
            ?? throw ApiProblemException.NotFound("invite_not_found", "Invite not found or already used.");

        var now = clock.UtcNow;

        if (invite.ExpiresAtUtc < now)
            throw ApiProblemException.BadRequest("invite_expired", "Invite has expired.");

        var existingMember = await db.HouseholdMembers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.HouseholdId == invite.HouseholdId && m.UserId == userId, ct);

        if (existingMember is { IsDeleted: false })
            return await GetByIdAsync(invite.HouseholdId, ct);

        var memberCount = await db.HouseholdMembers
            .CountAsync(m => m.HouseholdId == invite.HouseholdId && !m.IsDeleted, ct);

        if (memberCount >= 2)
            throw ApiProblemException.Conflict("household_full", "Household already has 2 members.");

        invite.Accepted = true;

        if (existingMember is null)
        {
            var member = new HouseholdMember
            {
                HouseholdId = invite.HouseholdId,
                UserId = userId,
                Role = MemberRole.Member,
                JoinedAtUtc = now,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            db.HouseholdMembers.Add(member);
        }
        else
        {
            existingMember.IsDeleted = false;
            existingMember.Role = MemberRole.Member;
            existingMember.JoinedAtUtc = now;
            existingMember.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);

        return await GetByIdAsync(invite.HouseholdId, ct);
    }

    public async Task<HouseholdDto> UpdateMemberRoleAsync(
        Guid householdId,
        Guid actorUserId,
        Guid memberUserId,
        UpdateHouseholdMemberRoleDto dto,
        CancellationToken ct)
    {
        await EnsureAdminMemberAsync(householdId, actorUserId, ct);

        var targetMember = await db.HouseholdMembers
            .FirstOrDefaultAsync(m => m.HouseholdId == householdId && m.UserId == memberUserId && !m.IsDeleted, ct)
            ?? throw ApiProblemException.NotFound("household_member_not_found", "Household member not found.");

        if (targetMember.Role == dto.Role)
            return await GetByIdAsync(householdId, ct);

        if (targetMember.Role == MemberRole.Admin && dto.Role != MemberRole.Admin)
        {
            var otherAdminExists = await db.HouseholdMembers.AnyAsync(
                m => m.HouseholdId == householdId
                    && m.UserId != memberUserId
                    && m.Role == MemberRole.Admin
                    && !m.IsDeleted,
                ct);

            if (!otherAdminExists)
                throw ApiProblemException.Conflict("last_admin_required", "Household must have at least one admin.");
        }

        targetMember.Role = dto.Role;
        await db.SaveChangesAsync(ct);

        return await GetByIdAsync(householdId, ct);
    }

    public async Task<HouseholdDto> RemoveMemberAsync(
        Guid householdId,
        Guid actorUserId,
        Guid memberUserId,
        CancellationToken ct)
    {
        await EnsureAdminMemberAsync(householdId, actorUserId, ct);

        if (actorUserId == memberUserId)
            throw ApiProblemException.BadRequest("household_self_remove_not_allowed", "Use the leave action to remove yourself from the household.");

        var targetMember = await db.HouseholdMembers
            .FirstOrDefaultAsync(m => m.HouseholdId == householdId && m.UserId == memberUserId && !m.IsDeleted, ct)
            ?? throw ApiProblemException.NotFound("household_member_not_found", "Household member not found.");

        targetMember.IsDeleted = true;
        targetMember.UpdatedAtUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        return await GetByIdAsync(householdId, ct);
    }

    public async Task LeaveAsync(Guid householdId, Guid userId, CancellationToken ct)
    {
        var departingMember = await EnsureMemberAsync(householdId, userId, ct);
        var now = clock.UtcNow;

        departingMember.IsDeleted = true;
        departingMember.UpdatedAtUtc = now;

        var remainingMembers = await db.HouseholdMembers
            .Where(m => m.HouseholdId == householdId && m.UserId != userId && !m.IsDeleted)
            .OrderBy(m => m.JoinedAtUtc)
            .ToListAsync(ct);

        if (remainingMembers.Count == 0)
        {
            var household = await db.Households
                .FirstOrDefaultAsync(h => h.Id == householdId && !h.IsDeleted, ct)
                ?? throw ApiProblemException.NotFound("household_not_found", "Household not found.");

            household.IsDeleted = true;
            household.UpdatedAtUtc = now;

            var activeInvites = await db.HouseholdInvites
                .Where(i => i.HouseholdId == householdId && !i.IsDeleted)
                .ToListAsync(ct);

            foreach (var invite in activeInvites)
            {
                invite.IsDeleted = true;
                invite.UpdatedAtUtc = now;
            }
        }
        else if (departingMember.Role == MemberRole.Admin && remainingMembers.All(member => member.Role != MemberRole.Admin))
        {
            remainingMembers[0].Role = MemberRole.Admin;
            remainingMembers[0].UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<HouseholdMember> EnsureMemberAsync(Guid householdId, Guid userId, CancellationToken ct)
    {
        return await db.HouseholdMembers
            .FirstOrDefaultAsync(m => m.HouseholdId == householdId && m.UserId == userId && !m.IsDeleted, ct)
            ?? throw ApiProblemException.Forbidden("household_access_denied", "You do not have access to this household.");
    }

    private async Task<HouseholdMember> EnsureAdminMemberAsync(Guid householdId, Guid userId, CancellationToken ct)
    {
        var member = await EnsureMemberAsync(householdId, userId, ct);
        if (member.Role != MemberRole.Admin)
            throw ApiProblemException.Forbidden("household_admin_required", "Only household admins can perform this action.");

        return member;
    }

    private async Task EnsureHouseholdCanAcceptInviteAsync(Guid householdId, CancellationToken ct)
    {
        var memberCount = await db.HouseholdMembers
            .CountAsync(m => m.HouseholdId == householdId && !m.IsDeleted, ct);

        if (memberCount >= 2)
            throw ApiProblemException.Conflict("household_full", "Household already has 2 members.");
    }

    private static HouseholdInvite CreateInvite(Guid householdId, DateTime now) => new()
    {
        HouseholdId = householdId,
        ExpiresAtUtc = now.Add(InviteTokenLifetime),
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };

    private static InviteResponseDto MapInvite(HouseholdInvite invite) => new(invite.Id, invite.Token, invite.ExpiresAtUtc);

    private static HouseholdDto MapHousehold(Household h) => new(
        h.Id, h.Name, h.TimeZoneId,
        h.Members.Where(m => !m.IsDeleted).Select(m => new HouseholdMemberDto(
            m.UserId, m.User.DisplayName, m.User.Username, m.Role, m.JoinedAtUtc)).ToList());
}
