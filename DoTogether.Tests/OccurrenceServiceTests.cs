using DoTogether.Application.Interfaces;
using DoTogether.Application.Services;
using DoTogether.Domain.Entities;
using DoTogether.Domain.Enums;
using DoTogether.Domain.ValueObjects;
using DoTogether.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Tests;

public class OccurrenceServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly OccurrenceService _service;
    private readonly Guid _householdId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private const string TimeZone = "Etc/UTC";

    public OccurrenceServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        var clock = new FakeDateTimeProvider(new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc));
        _service = new OccurrenceService(_db, clock);

        SeedBase();
    }

    private void SeedBase()
    {
        _db.Users.Add(new User { Id = _userId, Username = "tester", DisplayName = "T" });
        _db.Households.Add(new Household { Id = _householdId, Name = "H", TimeZoneId = TimeZone });
        _db.SaveChanges();
    }

    [Fact]
    public async Task GenerateAsync_Creates90DaysOfDailyOccurrences()
    {
        var template = new ChoreTemplate
        {
            HouseholdId = _householdId,
            Title = "Daily",
            RecurrenceRule = new RecurrenceRule { Type = RecurrenceType.Daily, Interval = 1 },
            CreatedByUserId = _userId,
            StartDate = new DateOnly(2025, 6, 15)
        };
        _db.ChoreTemplates.Add(template);
        await _db.SaveChangesAsync();

        await _service.GenerateAsync(template, TimeZone);
        await _db.SaveChangesAsync();

        var count = await _db.ChoreOccurrences.CountAsync(o => o.ChoreTemplateId == template.Id);
        // From Jun 15 to Sep 13 = 91 days
        Assert.Equal(91, count);
        Assert.Equal(new DateOnly(2025, 9, 13), template.GeneratedThroughDate);
    }

    [Fact]
    public async Task GenerateAsync_DoesNotDuplicateExisting()
    {
        var template = new ChoreTemplate
        {
            HouseholdId = _householdId,
            Title = "NoDupes",
            RecurrenceRule = new RecurrenceRule { Type = RecurrenceType.Daily, Interval = 1 },
            CreatedByUserId = _userId,
            StartDate = new DateOnly(2025, 6, 15)
        };
        _db.ChoreTemplates.Add(template);
        await _db.SaveChangesAsync();

        await _service.GenerateAsync(template, TimeZone);
        await _db.SaveChangesAsync();

        var countFirst = await _db.ChoreOccurrences.CountAsync(o => o.ChoreTemplateId == template.Id);

        // Generate again – should not duplicate.
        await _service.GenerateAsync(template, TimeZone);
        await _db.SaveChangesAsync();

        var countSecond = await _db.ChoreOccurrences.CountAsync(o => o.ChoreTemplateId == template.Id);
        Assert.Equal(countFirst, countSecond);
    }

    [Fact]
    public async Task GenerateAsync_RespectsEndDate()
    {
        var template = new ChoreTemplate
        {
            HouseholdId = _householdId,
            Title = "Limited",
            RecurrenceRule = new RecurrenceRule { Type = RecurrenceType.Daily, Interval = 1 },
            CreatedByUserId = _userId,
            StartDate = new DateOnly(2025, 6, 15),
            EndDate = new DateOnly(2025, 6, 20)
        };
        _db.ChoreTemplates.Add(template);
        await _db.SaveChangesAsync();

        await _service.GenerateAsync(template, TimeZone);
        await _db.SaveChangesAsync();

        var count = await _db.ChoreOccurrences.CountAsync(o => o.ChoreTemplateId == template.Id);
        Assert.Equal(6, count); // Jun 15–20 inclusive
    }

    [Fact]
    public async Task MarkMissedAsync_MarksOverduePendingAsMissed()
    {
        var template = new ChoreTemplate
        {
            HouseholdId = _householdId,
            Title = "Missed",
            RecurrenceRule = new RecurrenceRule { Type = RecurrenceType.Once },
            CreatedByUserId = _userId,
            StartDate = new DateOnly(2025, 6, 10)
        };
        _db.ChoreTemplates.Add(template);

        var occ = new ChoreOccurrence
        {
            ChoreTemplateId = template.Id,
            HouseholdId = _householdId,
            DueDate = new DateOnly(2025, 6, 10), // Before "today" (Jun 15)
            Status = OccurrenceStatus.Pending
        };
        _db.ChoreOccurrences.Add(occ);
        await _db.SaveChangesAsync();

        await _service.MarkMissedAsync(_householdId, TimeZone);
        await _db.SaveChangesAsync();

        var updated = await _db.ChoreOccurrences.FirstAsync(o => o.Id == occ.Id);
        Assert.Equal(OccurrenceStatus.Missed, updated.Status);
    }

    [Fact]
    public async Task MarkMissedAsync_DoesNotTouchCompleted()
    {
        var template = new ChoreTemplate
        {
            HouseholdId = _householdId,
            Title = "Done",
            RecurrenceRule = new RecurrenceRule { Type = RecurrenceType.Once },
            CreatedByUserId = _userId,
            StartDate = new DateOnly(2025, 6, 10)
        };
        _db.ChoreTemplates.Add(template);

        var occ = new ChoreOccurrence
        {
            ChoreTemplateId = template.Id,
            HouseholdId = _householdId,
            DueDate = new DateOnly(2025, 6, 10),
            Status = OccurrenceStatus.Completed
        };
        _db.ChoreOccurrences.Add(occ);
        await _db.SaveChangesAsync();

        await _service.MarkMissedAsync(_householdId, TimeZone);
        await _db.SaveChangesAsync();

        var updated = await _db.ChoreOccurrences.FirstAsync(o => o.Id == occ.Id);
        Assert.Equal(OccurrenceStatus.Completed, updated.Status);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
