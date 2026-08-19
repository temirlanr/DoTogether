using DoTogether.Application.DTOs;
using DoTogether.Application.Exceptions;
using DoTogether.Application.Services;
using DoTogether.Domain.Entities;
using DoTogether.Domain.Enums;
using DoTogether.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Tests;

public class HouseholdServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly HouseholdService _service;

    private readonly Guid _householdId = Guid.NewGuid();
    private readonly Guid _adminUserId = Guid.NewGuid();
    private readonly Guid _memberUserId = Guid.NewGuid();
    private readonly Guid _outsiderUserId = Guid.NewGuid();
    private readonly DateTime _now = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    public HouseholdServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _service = new HouseholdService(_db, new FakeDateTimeProvider(_now));

        SeedBaseData();
    }

    [Fact]
    public async Task UpdateMemberRoleAsync_PromotesOtherMemberToAdmin()
    {
        var updated = await _service.UpdateMemberRoleAsync(
            _householdId,
            _adminUserId,
            _memberUserId,
            new UpdateHouseholdMemberRoleDto(MemberRole.Admin),
            CancellationToken.None);

        var promotedMember = Assert.Single(updated.Members, member => member.UserId == _memberUserId);
        Assert.Equal(MemberRole.Admin, promotedMember.Role);
    }

    [Fact]
    public async Task UpdateMemberRoleAsync_PreventsRemovingLastAdmin()
    {
        var error = await Assert.ThrowsAsync<ApiProblemException>(() =>
            _service.UpdateMemberRoleAsync(
                _householdId,
                _adminUserId,
                _adminUserId,
                new UpdateHouseholdMemberRoleDto(MemberRole.Member),
                CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Equal("last_admin_required", error.ErrorCode);
    }

    [Fact]
    public async Task UpdateMemberRoleAsync_RejectsNonAdminActor()
    {
        var error = await Assert.ThrowsAsync<ApiProblemException>(() =>
            _service.UpdateMemberRoleAsync(
                _householdId,
                _memberUserId,
                _adminUserId,
                new UpdateHouseholdMemberRoleDto(MemberRole.Member),
                CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
        Assert.Equal("household_admin_required", error.ErrorCode);
    }

    [Fact]
    public async Task UpdateAsync_RenamesHouseholdForAdmin()
    {
        var updated = await _service.UpdateAsync(
            _householdId,
            _adminUserId,
            new UpdateHouseholdDto("Renamed Home"),
            CancellationToken.None);

        Assert.Equal("Renamed Home", updated.Name);
    }

    [Fact]
    public async Task RemoveMemberAsync_KicksSelectedMember()
    {
        var updated = await _service.RemoveMemberAsync(_householdId, _adminUserId, _memberUserId, CancellationToken.None);

        Assert.Single(updated.Members);
        Assert.DoesNotContain(updated.Members, member => member.UserId == _memberUserId);
    }

    [Fact]
    public async Task JoinAsync_ReactivatesRemovedMemberUsingInviteToken()
    {
        await _service.RemoveMemberAsync(_householdId, _adminUserId, _memberUserId, CancellationToken.None);

        var rejoinTime = _now.AddHours(1);
        var service = new HouseholdService(_db, new FakeDateTimeProvider(rejoinTime));
        var invite = await service.GetInviteTokenAsync(_householdId, _adminUserId, CancellationToken.None);

        var joined = await service.JoinAsync(_memberUserId, new JoinHouseholdDto(invite.Token), CancellationToken.None);

        Assert.Equal(2, joined.Members.Count);
        var rejoinedMember = Assert.Single(joined.Members, member => member.UserId == _memberUserId);
        Assert.Equal(MemberRole.Member, rejoinedMember.Role);
        Assert.Equal(rejoinTime, rejoinedMember.JoinedAtUtc);

        var memberRow = await _db.HouseholdMembers
            .IgnoreQueryFilters()
            .SingleAsync(member => member.HouseholdId == _householdId && member.UserId == _memberUserId);

        Assert.False(memberRow.IsDeleted);
        Assert.Equal(rejoinTime, memberRow.JoinedAtUtc);

        var acceptedInvite = await _db.HouseholdInvites.SingleAsync(i => i.Id == invite.InviteId);
        Assert.True(acceptedInvite.Accepted);
    }

    [Fact]
    public async Task LeaveAsync_PromotesRemainingMemberWhenAdminLeaves()
    {
        await _service.LeaveAsync(_householdId, _adminUserId, CancellationToken.None);

        var household = await _service.GetByIdAsync(_householdId, CancellationToken.None);
        var remainingMember = Assert.Single(household.Members);

        Assert.Equal(_memberUserId, remainingMember.UserId);
        Assert.Equal(MemberRole.Admin, remainingMember.Role);
    }

    [Fact]
    public async Task LeaveAsync_DeletesHouseholdWhenLastMemberLeaves()
    {
        var householdId = SeedOnePersonHousehold();

        await _service.LeaveAsync(householdId, _adminUserId, CancellationToken.None);

        var deletedHousehold = await _db.Households.IgnoreQueryFilters().SingleAsync(h => h.Id == householdId);
        Assert.True(deletedHousehold.IsDeleted);
    }

    [Fact]
    public async Task CreateAsync_CreatesInitialInviteToken()
    {
        var household = await _service.CreateAsync(
            _outsiderUserId,
            new CreateHouseholdDto("New Home", "UTC"),
            CancellationToken.None);

        var invite = await _db.HouseholdInvites.SingleAsync(i => i.HouseholdId == household.Id);

        Assert.False(invite.Accepted);
        Assert.False(invite.IsDeleted);
        Assert.NotEmpty(invite.Token);
        Assert.Equal(_now.AddDays(7), invite.ExpiresAtUtc);
    }

    [Fact]
    public async Task GetInviteTokenAsync_CreatesTokenForLegacyHouseholdWithoutOne()
    {
        var householdId = SeedOnePersonHousehold();

        var invite = await _service.GetInviteTokenAsync(householdId, _adminUserId, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, invite.InviteId);
        Assert.NotEmpty(invite.Token);
        Assert.Equal(_now.AddDays(7), invite.ExpiresAtUtc);
    }

    [Fact]
    public async Task GetInviteTokenAsync_AllowsMembersToViewInviteToken()
    {
        var householdId = SeedOnePersonHousehold(primaryUserId: _memberUserId, primaryRole: MemberRole.Member);

        var invite = await _service.GetInviteTokenAsync(householdId, _memberUserId, CancellationToken.None);

        Assert.NotEmpty(invite.Token);
    }

    [Fact]
    public async Task RegenerateInviteTokenAsync_ReplacesCurrentToken()
    {
        var householdId = SeedOnePersonHousehold();
        var firstInvite = await _service.GetInviteTokenAsync(householdId, _adminUserId, CancellationToken.None);

        var regeneratedInvite = await _service.RegenerateInviteTokenAsync(householdId, _adminUserId, CancellationToken.None);

        Assert.NotEqual(firstInvite.InviteId, regeneratedInvite.InviteId);
        Assert.NotEqual(firstInvite.Token, regeneratedInvite.Token);

        var originalInvite = await _db.HouseholdInvites
            .IgnoreQueryFilters()
            .SingleAsync(invite => invite.Id == firstInvite.InviteId);

        Assert.True(originalInvite.IsDeleted);
    }

    private void SeedBaseData()
    {
        var household = new Household
        {
            Id = _householdId,
            Name = "DoTogether Home",
            TimeZoneId = "UTC",
        };

        var adminUser = new User
        {
            Id = _adminUserId,
            Username = "owner",
            DisplayName = "Owner",
            PasswordHash = "hash",
        };

        var memberUser = new User
        {
            Id = _memberUserId,
            Username = "partner",
            DisplayName = "Partner",
            PasswordHash = "hash",
        };

        var outsiderUser = new User
        {
            Id = _outsiderUserId,
            Username = "outsider",
            DisplayName = "Outsider",
            PasswordHash = "hash",
        };

        _db.Users.AddRange(adminUser, memberUser, outsiderUser);
        _db.Households.Add(household);
        _db.HouseholdMembers.AddRange(
            new HouseholdMember
            {
                HouseholdId = _householdId,
                UserId = _adminUserId,
                Role = MemberRole.Admin,
                JoinedAtUtc = _now,
            },
            new HouseholdMember
            {
                HouseholdId = _householdId,
                UserId = _memberUserId,
                Role = MemberRole.Member,
                JoinedAtUtc = _now,
            });

        _db.SaveChanges();
    }

    private Guid SeedOnePersonHousehold(
        bool includeMember = false,
        Guid? primaryUserId = null,
        MemberRole primaryRole = MemberRole.Admin)
    {
        var householdId = Guid.NewGuid();
        var primaryMemberUserId = primaryUserId ?? _adminUserId;

        _db.Households.Add(new Household
        {
            Id = householdId,
            Name = "Invite Home",
            TimeZoneId = "UTC",
        });
        _db.HouseholdMembers.Add(new HouseholdMember
        {
            HouseholdId = householdId,
            UserId = primaryMemberUserId,
            Role = primaryRole,
            JoinedAtUtc = _now,
        });

        if (includeMember)
        {
            _db.HouseholdMembers.Add(new HouseholdMember
            {
                HouseholdId = householdId,
                UserId = _memberUserId,
                Role = MemberRole.Member,
                JoinedAtUtc = _now,
            });
        }
        _db.SaveChanges();

        return householdId;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}