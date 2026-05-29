using DoTogether.Application.DTOs;
using DoTogether.Application.Services;
using DoTogether.Domain.Entities;
using DoTogether.Domain.Enums;
using DoTogether.Domain.ValueObjects;
using DoTogether.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Tests;

public class ChoreServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ChoreService _service;
    private readonly CalendarService _calendarService;
    private readonly Guid _householdId = Guid.NewGuid();
    private readonly Guid _creatorId = Guid.NewGuid();
    private readonly Guid _assigneeId = Guid.NewGuid();
    private readonly Guid _newAssigneeId = Guid.NewGuid();
    private const string TimeZone = "Etc/UTC";

    public ChoreServiceTests()
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
        _service = new ChoreService(_db, clock, occurrenceService);
        _calendarService = new CalendarService(_db, occurrenceService);

        SeedBase();
    }

    [Fact]
    public async Task UpdateTemplateAsync_ClearsAssignee_WhenExplicitlySetToNull()
    {
        var template = await CreateTemplateAsync(_assigneeId);
        var pendingFutureOccurrence = await AddOccurrenceAsync(template.Id, new DateOnly(2025, 6, 16), OccurrenceStatus.Pending, _assigneeId);
        var completedFutureOccurrence = await AddOccurrenceAsync(template.Id, new DateOnly(2025, 6, 17), OccurrenceStatus.Completed, _assigneeId);

        var dto = new UpdateChoreTemplateDto
        {
            AssigneeId = null,
        };

        var result = await _service.UpdateTemplateAsync(template.Id, _householdId, dto, CancellationToken.None);

        var updatedTemplate = await _db.ChoreTemplates.FirstAsync(t => t.Id == template.Id);
        var updatedPendingOccurrence = await _db.ChoreOccurrences.FirstAsync(o => o.Id == pendingFutureOccurrence.Id);
        var unchangedCompletedOccurrence = await _db.ChoreOccurrences.FirstAsync(o => o.Id == completedFutureOccurrence.Id);

        Assert.Null(result.AssigneeId);
        Assert.Null(result.AssigneeName);
        Assert.Null(updatedTemplate.AssigneeId);
        Assert.Null(updatedPendingOccurrence.AssigneeId);
        Assert.Equal(1, updatedPendingOccurrence.Version);
        Assert.Equal(_assigneeId, unchangedCompletedOccurrence.AssigneeId);
        Assert.Equal(0, unchangedCompletedOccurrence.Version);
    }

    [Fact]
    public async Task UpdateTemplateAsync_ReassignsFuturePendingOccurrences_WhenAssigneeChanges()
    {
        var template = await CreateTemplateAsync(_assigneeId);
        var pendingFutureOccurrence = await AddOccurrenceAsync(template.Id, new DateOnly(2025, 6, 16), OccurrenceStatus.Pending, _assigneeId);
        var missedPastOccurrence = await AddOccurrenceAsync(template.Id, new DateOnly(2025, 6, 14), OccurrenceStatus.Missed, _assigneeId);

        var dto = new UpdateChoreTemplateDto
        {
            AssigneeId = _newAssigneeId,
        };

        var result = await _service.UpdateTemplateAsync(template.Id, _householdId, dto, CancellationToken.None);

        var updatedTemplate = await _db.ChoreTemplates.FirstAsync(t => t.Id == template.Id);
        var updatedPendingOccurrence = await _db.ChoreOccurrences.FirstAsync(o => o.Id == pendingFutureOccurrence.Id);
        var unchangedMissedOccurrence = await _db.ChoreOccurrences.FirstAsync(o => o.Id == missedPastOccurrence.Id);

        Assert.Equal(_newAssigneeId, result.AssigneeId);
        Assert.Equal("New Assignee", result.AssigneeName);
        Assert.Equal(_newAssigneeId, updatedTemplate.AssigneeId);
        Assert.Equal(_newAssigneeId, updatedPendingOccurrence.AssigneeId);
        Assert.Equal(1, updatedPendingOccurrence.Version);
        Assert.Equal(_assigneeId, unchangedMissedOccurrence.AssigneeId);
        Assert.Equal(0, unchangedMissedOccurrence.Version);
    }

    [Fact]
    public async Task SoftDeleteTemplateAsync_DeletesNonCompletedOccurrences_AndKeepsCompleted()
    {
        var template = await CreateTemplateAsync(_assigneeId);
        var pendingOccurrence = await AddOccurrenceAsync(template.Id, new DateOnly(2025, 6, 16), OccurrenceStatus.Pending, _assigneeId);
        var completedOccurrence = await AddOccurrenceAsync(template.Id, new DateOnly(2025, 6, 17), OccurrenceStatus.Completed, _assigneeId);
        var skippedOccurrence = await AddOccurrenceAsync(template.Id, new DateOnly(2025, 6, 18), OccurrenceStatus.Skipped, _assigneeId);
        var missedOccurrence = await AddOccurrenceAsync(template.Id, new DateOnly(2025, 6, 14), OccurrenceStatus.Missed, _assigneeId);

        await _service.SoftDeleteTemplateAsync(template.Id, _householdId, CancellationToken.None);

        var deletedTemplate = await _db.ChoreTemplates.IgnoreQueryFilters().FirstAsync(t => t.Id == template.Id);
        var allOccurrences = await _db.ChoreOccurrences
            .IgnoreQueryFilters()
            .Where(o => o.ChoreTemplateId == template.Id)
            .ToDictionaryAsync(o => o.Id);

        Assert.True(deletedTemplate.IsDeleted);
        Assert.False(deletedTemplate.IsActive);
        Assert.True(allOccurrences[pendingOccurrence.Id].IsDeleted);
        Assert.True(allOccurrences[skippedOccurrence.Id].IsDeleted);
        Assert.True(allOccurrences[missedOccurrence.Id].IsDeleted);
        Assert.False(allOccurrences[completedOccurrence.Id].IsDeleted);
        Assert.Equal(1, allOccurrences[pendingOccurrence.Id].Version);
        Assert.Equal(0, allOccurrences[completedOccurrence.Id].Version);
    }

    [Fact]
    public async Task GetOccurrencesAsync_ReturnsCompletedOccurrences_WhenTemplateWasSoftDeleted()
    {
        var template = await CreateTemplateAsync(_assigneeId);
        var completedOccurrence = await AddOccurrenceAsync(template.Id, new DateOnly(2025, 6, 17), OccurrenceStatus.Completed, _assigneeId);

        await _service.SoftDeleteTemplateAsync(template.Id, _householdId, CancellationToken.None);

        var result = await _calendarService.GetOccurrencesAsync(
            _householdId,
            TimeZone,
            completedOccurrence.DueDate,
            completedOccurrence.DueDate,
            null,
            CancellationToken.None);

        var occurrence = Assert.Single(result);
        Assert.Equal(completedOccurrence.Id, occurrence.Id);
        Assert.Equal(template.Title, occurrence.ChoreTitle);
        Assert.Equal(OccurrenceStatus.Completed, occurrence.Status);
    }

    private void SeedBase()
    {
        _db.Users.AddRange(
            new User { Id = _creatorId, Username = "creator", DisplayName = "Creator" },
            new User { Id = _assigneeId, Username = "assignee", DisplayName = "Current Assignee" },
            new User { Id = _newAssigneeId, Username = "new-assignee", DisplayName = "New Assignee" });
        _db.Households.Add(new Household { Id = _householdId, Name = "Home", TimeZoneId = TimeZone });
        _db.SaveChanges();
    }

    private async Task<ChoreTemplate> CreateTemplateAsync(Guid? assigneeId)
    {
        var template = new ChoreTemplate
        {
            HouseholdId = _householdId,
            Title = "Laundry",
            RecurrenceRule = new RecurrenceRule { Type = RecurrenceType.Daily, Interval = 1 },
            CreatedByUserId = _creatorId,
            AssigneeId = assigneeId,
            StartDate = new DateOnly(2025, 6, 15)
        };

        _db.ChoreTemplates.Add(template);
        await _db.SaveChangesAsync();
        return template;
    }

    private async Task<ChoreOccurrence> AddOccurrenceAsync(Guid templateId, DateOnly dueDate, OccurrenceStatus status, Guid? assigneeId)
    {
        var occurrence = new ChoreOccurrence
        {
            ChoreTemplateId = templateId,
            HouseholdId = _householdId,
            DueDate = dueDate,
            Status = status,
            AssigneeId = assigneeId,
            Version = 0,
        };

        _db.ChoreOccurrences.Add(occurrence);
        await _db.SaveChangesAsync();
        return occurrence;
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}