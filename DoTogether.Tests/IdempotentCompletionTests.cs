using DoTogether.Application.Interfaces;
using DoTogether.Application.Services;
using DoTogether.Domain.Entities;
using DoTogether.Domain.Enums;
using DoTogether.Domain.ValueObjects;
using DoTogether.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Tests;

public class IdempotentCompletionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ChoreService _choreService;
    private readonly Guid _householdId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _occurrenceId = Guid.NewGuid();

    public IdempotentCompletionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        var clock = new FakeDateTimeProvider(new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc));
        var occurrenceService = new OccurrenceService(_db, clock);
        _choreService = new ChoreService(_db, clock, occurrenceService);

        SeedTestData();
    }

    private void SeedTestData()
    {
        var user = new User { Id = _userId, Username = "testuser", DisplayName = "Test" };
        _db.Users.Add(user);

        var household = new Household { Id = _householdId, Name = "Test Home", TimeZoneId = "Etc/UTC" };
        _db.Households.Add(household);

        _db.HouseholdMembers.Add(new HouseholdMember
        {
            HouseholdId = _householdId,
            UserId = _userId,
            Role = MemberRole.Admin
        });

        var template = new ChoreTemplate
        {
            Id = Guid.NewGuid(),
            HouseholdId = _householdId,
            Title = "Test Chore",
            RecurrenceRule = new RecurrenceRule { Type = RecurrenceType.Daily, Interval = 1 },
            CreatedByUserId = _userId,
            StartDate = new DateOnly(2025, 6, 1)
        };
        _db.ChoreTemplates.Add(template);

        _db.ChoreOccurrences.Add(new ChoreOccurrence
        {
            Id = _occurrenceId,
            ChoreTemplateId = template.Id,
            HouseholdId = _householdId,
            AssigneeId = _userId,
            DueDate = new DateOnly(2025, 6, 15),
            Status = OccurrenceStatus.Pending
        });

        _db.SaveChanges();
    }

    [Fact]
    public async Task Complete_FirstCall_ChangesStatusToCompleted()
    {
        var clientOpId = Guid.NewGuid();

        var result = await _choreService.MutateOccurrenceAsync(
            _occurrenceId, _householdId, _userId,
            ChoreEventType.Completed, clientOpId, null, CancellationToken.None);

        Assert.Equal(OccurrenceStatus.Completed, result.Status);
        Assert.Single(result.Events);
        Assert.Equal(ChoreEventType.Completed, result.Events[0].EventType);
    }

    [Fact]
    public async Task Complete_DuplicateClientOperationId_ReturnsOriginalResponse()
    {
        var clientOpId = Guid.NewGuid();

        var first = await _choreService.MutateOccurrenceAsync(
            _occurrenceId, _householdId, _userId,
            ChoreEventType.Completed, clientOpId, null, CancellationToken.None);

        var second = await _choreService.MutateOccurrenceAsync(
            _occurrenceId, _householdId, _userId,
            ChoreEventType.Completed, clientOpId, null, CancellationToken.None);

        // Same response returned – no second event created.
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.Events.Count, second.Events.Count);
    }

    [Fact]
    public async Task Complete_ThenUndo_SetsBackToPending()
    {
        var completeOpId = Guid.NewGuid();
        var undoOpId = Guid.NewGuid();

        await _choreService.MutateOccurrenceAsync(
            _occurrenceId, _householdId, _userId,
            ChoreEventType.Completed, completeOpId, null, CancellationToken.None);

        var result = await _choreService.MutateOccurrenceAsync(
            _occurrenceId, _householdId, _userId,
            ChoreEventType.Undone, undoOpId, null, CancellationToken.None);

        Assert.Equal(OccurrenceStatus.Pending, result.Status);
        Assert.Equal(2, result.Events.Count);
    }

    [Fact]
    public async Task Skip_SetsStatusToSkipped()
    {
        var clientOpId = Guid.NewGuid();

        var result = await _choreService.MutateOccurrenceAsync(
            _occurrenceId, _householdId, _userId,
            ChoreEventType.Skipped, clientOpId, null, CancellationToken.None);

        Assert.Equal(OccurrenceStatus.Skipped, result.Status);
    }

    [Fact]
    public async Task Reassign_ChangesAssignee_AndRecordsEvent()
    {
        var newAssignee = new User { Id = Guid.NewGuid(), Username = "other", DisplayName = "Other" };
        _db.Users.Add(newAssignee);
        _db.HouseholdMembers.Add(new HouseholdMember
        {
            HouseholdId = _householdId,
            UserId = newAssignee.Id,
            Role = MemberRole.Member
        });
        await _db.SaveChangesAsync();

        var clientOpId = Guid.NewGuid();

        var result = await _choreService.ReassignOccurrenceAsync(
            _occurrenceId, _householdId, _userId,
            newAssignee.Id, clientOpId, CancellationToken.None);

        Assert.Equal(newAssignee.Id, result.AssigneeId);
        Assert.Single(result.Events);
        Assert.Equal(ChoreEventType.Reassigned, result.Events[0].EventType);
        Assert.Contains("previousAssigneeId", result.Events[0].Metadata!);
    }

    [Fact]
    public async Task Reassign_DuplicateClientOperationId_ReturnsOriginalResponse()
    {
        var newAssignee = new User { Id = Guid.NewGuid(), Username = "other2", DisplayName = "Other2" };
        _db.Users.Add(newAssignee);
        _db.HouseholdMembers.Add(new HouseholdMember
        {
            HouseholdId = _householdId,
            UserId = newAssignee.Id,
            Role = MemberRole.Member
        });
        await _db.SaveChangesAsync();

        var clientOpId = Guid.NewGuid();

        var first = await _choreService.ReassignOccurrenceAsync(
            _occurrenceId, _householdId, _userId,
            newAssignee.Id, clientOpId, CancellationToken.None);

        var second = await _choreService.ReassignOccurrenceAsync(
            _occurrenceId, _householdId, _userId,
            newAssignee.Id, clientOpId, CancellationToken.None);

        Assert.Equal(first.AssigneeId, second.AssigneeId);
        Assert.Equal(first.Version, second.Version);
    }

    [Fact]
    public async Task VersionIncrementsOnEachMutation()
    {
        var r1 = await _choreService.MutateOccurrenceAsync(
            _occurrenceId, _householdId, _userId,
            ChoreEventType.Completed, Guid.NewGuid(), null, CancellationToken.None);

        var r2 = await _choreService.MutateOccurrenceAsync(
            _occurrenceId, _householdId, _userId,
            ChoreEventType.Undone, Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(1, r1.Version);
        Assert.Equal(2, r2.Version);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}

internal sealed class FakeDateTimeProvider(DateTime utcNow) : IDateTimeProvider
{
    public DateTime UtcNow => utcNow;

    public DateOnly TodayIn(string ianaTimeZoneId)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);
        return DateOnly.FromDateTime(local);
    }
}
