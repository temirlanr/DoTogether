using DoTogether.Application.Interfaces;
using DoTogether.Application.Services;
using DoTogether.Domain.Entities;
using DoTogether.Domain.Enums;
using DoTogether.Domain.ValueObjects;
using DoTogether.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Tests;

public class AchievementServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly AchievementService _svc;
    private readonly ChoreService _choreService;

    private readonly Guid _householdId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _partnerId = Guid.NewGuid();
    private readonly Guid _templateId = Guid.NewGuid();

    // America/New_York: EDT (UTC-4) in summer, EST (UTC-5) in winter.
    private const string TZ = "America/New_York";

    // Default clock: June 15, 2025 3:00 PM UTC  =  11:00 AM ET.
    private readonly FakeDateTimeProvider _clock =
        new(new DateTime(2025, 6, 15, 15, 0, 0, DateTimeKind.Utc));

    public AchievementServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _svc = new AchievementService(_db, _clock);
        var occurrenceService = new OccurrenceService(_db, _clock);
        _choreService = new ChoreService(_db, _clock, occurrenceService);

        SeedBaseData();
    }

    private void SeedBaseData()
    {
        _db.Users.AddRange(
            new User { Id = _userId, Email = "me@test.com", DisplayName = "Me" },
            new User { Id = _partnerId, Email = "partner@test.com", DisplayName = "Partner" });

        _db.Households.Add(new Household { Id = _householdId, Name = "Home", TimeZoneId = TZ });

        _db.HouseholdMembers.AddRange(
            new HouseholdMember { HouseholdId = _householdId, UserId = _userId, Role = MemberRole.Admin },
            new HouseholdMember { HouseholdId = _householdId, UserId = _partnerId, Role = MemberRole.Member });

        _db.ChoreTemplates.Add(new ChoreTemplate
        {
            Id = _templateId,
            HouseholdId = _householdId,
            Title = "Test Chore",
            RecurrenceRule = new RecurrenceRule { Type = RecurrenceType.Daily, Interval = 1 },
            CreatedByUserId = _userId,
            StartDate = new DateOnly(2025, 6, 1)
        });

        _db.SaveChanges();
    }

    // ── Helpers ──

    private Guid AddOccurrence(
        DateOnly dueDate,
        Guid? assigneeId = null,
        OccurrenceStatus status = OccurrenceStatus.Pending)
    {
        var occ = new ChoreOccurrence
        {
            ChoreTemplateId = _templateId,
            HouseholdId = _householdId,
            AssigneeId = assigneeId ?? _userId,
            DueDate = dueDate,
            Status = status
        };
        _db.ChoreOccurrences.Add(occ);
        _db.SaveChanges();
        return occ.Id;
    }

    private void AddCompletionEvent(Guid occurrenceId, DateTime occurredAtUtc, Guid? actorId = null)
    {
        _db.ChoreEvents.Add(new ChoreEvent
        {
            ChoreOccurrenceId = occurrenceId,
            EventType = ChoreEventType.Completed,
            PerformedByUserId = actorId ?? _userId,
            OccurredAtUtc = occurredAtUtc,
            ClientOperationId = Guid.NewGuid()
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ───────────────────── Undo ─────────────────────

    [Fact]
    public async Task Summary_CompleteThenUndo_NotCounted()
    {
        var occId = AddOccurrence(new DateOnly(2025, 6, 15), _userId);

        await _choreService.MutateOccurrenceAsync(
            occId, _householdId, _userId, ChoreEventType.Completed,
            Guid.NewGuid(), null, CancellationToken.None);

        var afterComplete = await _svc.GetSummaryAsync(
            _householdId, TZ, new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 30), null, CancellationToken.None);
        Assert.Equal(1, afterComplete.TotalCompleted);

        await _choreService.MutateOccurrenceAsync(
            occId, _householdId, _userId, ChoreEventType.Undone,
            Guid.NewGuid(), null, CancellationToken.None);

        var afterUndo = await _svc.GetSummaryAsync(
            _householdId, TZ, new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 30), null, CancellationToken.None);
        Assert.Equal(0, afterUndo.TotalCompleted);
    }

    [Fact]
    public async Task Today_UndoneCompletion_NotShown()
    {
        var occId = AddOccurrence(new DateOnly(2025, 6, 15), _userId);

        await _choreService.MutateOccurrenceAsync(
            occId, _householdId, _userId, ChoreEventType.Completed,
            Guid.NewGuid(), null, CancellationToken.None);

        await _choreService.MutateOccurrenceAsync(
            occId, _householdId, _userId, ChoreEventType.Undone,
            Guid.NewGuid(), null, CancellationToken.None);

        var today = await _svc.GetTodayAsync(_householdId, TZ, null, CancellationToken.None);
        Assert.Equal(0, today.CompletedCountToday);
    }

    // ───────────────────── Double-submit ─────────────────────

    [Fact]
    public async Task Summary_DoubleSubmitSameClientOp_CountedOnce()
    {
        var occId = AddOccurrence(new DateOnly(2025, 6, 15), _userId);
        var opId = Guid.NewGuid();

        await _choreService.MutateOccurrenceAsync(
            occId, _householdId, _userId, ChoreEventType.Completed,
            opId, null, CancellationToken.None);

        await _choreService.MutateOccurrenceAsync(
            occId, _householdId, _userId, ChoreEventType.Completed,
            opId, null, CancellationToken.None);

        var summary = await _svc.GetSummaryAsync(
            _householdId, TZ, new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 30), null, CancellationToken.None);
        Assert.Equal(1, summary.TotalCompleted);
    }

    // ───────────────────── DST boundary (fall-back) ─────────────────────

    [Fact]
    public async Task Summary_DstFallBack_OnTimeBeforeMidnight()
    {
        // Nov 2 2025: America/New_York falls back UTC-4 → UTC-5 at 2 AM.
        // End of Nov 2 EST = Nov 3, 00:00 EST = Nov 3, 05:00 UTC.
        var occId = AddOccurrence(new DateOnly(2025, 11, 2), _userId, OccurrenceStatus.Completed);
        // 11:59 PM EST on Nov 2 = Nov 3, 04:59 UTC → on-time.
        AddCompletionEvent(occId, new DateTime(2025, 11, 3, 4, 59, 0, DateTimeKind.Utc));

        var novClock = new FakeDateTimeProvider(new DateTime(2025, 11, 3, 12, 0, 0, DateTimeKind.Utc));
        var svc = new AchievementService(_db, novClock);

        var summary = await svc.GetSummaryAsync(
            _householdId, TZ, new DateOnly(2025, 11, 1), new DateOnly(2025, 11, 30), null, CancellationToken.None);

        Assert.Equal(1, summary.OnTimeCompleted);
        Assert.Equal(0, summary.LateCompleted);
    }

    [Fact]
    public async Task Summary_DstFallBack_LateAfterMidnight()
    {
        var occId = AddOccurrence(new DateOnly(2025, 11, 2), _userId, OccurrenceStatus.Completed);
        // 12:01 AM EST on Nov 3 = Nov 3, 05:01 UTC → late for Nov 2 due date.
        AddCompletionEvent(occId, new DateTime(2025, 11, 3, 5, 1, 0, DateTimeKind.Utc));

        var novClock = new FakeDateTimeProvider(new DateTime(2025, 11, 3, 12, 0, 0, DateTimeKind.Utc));
        var svc = new AchievementService(_db, novClock);

        var summary = await svc.GetSummaryAsync(
            _householdId, TZ, new DateOnly(2025, 11, 1), new DateOnly(2025, 11, 30), null, CancellationToken.None);

        Assert.Equal(0, summary.OnTimeCompleted);
        Assert.Equal(1, summary.LateCompleted);
    }

    // ───────────────────── Late vs on-time classification ─────────────────────

    [Fact]
    public async Task Summary_OnTimeAndLate_ClassifiedCorrectly()
    {
        // June 15 EDT (UTC-4). End of June 15 = June 16, 04:00 UTC.

        // On-time: completed at 11 PM ET = June 16, 03:00 UTC (< 04:00 UTC).
        var onTimeId = AddOccurrence(new DateOnly(2025, 6, 15), _userId, OccurrenceStatus.Completed);
        AddCompletionEvent(onTimeId, new DateTime(2025, 6, 16, 3, 0, 0, DateTimeKind.Utc));

        // Late: completed at 1 AM ET on June 16 = June 16, 05:00 UTC (> 04:00 UTC).
        var lateId = AddOccurrence(new DateOnly(2025, 6, 15), _userId, OccurrenceStatus.Completed);
        AddCompletionEvent(lateId, new DateTime(2025, 6, 16, 5, 0, 0, DateTimeKind.Utc));

        var summary = await _svc.GetSummaryAsync(
            _householdId, TZ, new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 30), null, CancellationToken.None);

        Assert.Equal(1, summary.OnTimeCompleted);
        Assert.Equal(1, summary.LateCompleted);
    }

    // ───────────────────── Streaks ─────────────────────

    [Fact]
    public async Task Streaks_ConsecutiveDays_CorrectCurrentAndLongest()
    {
        // 3 consecutive days ending today (June 13-15).
        for (int day = 13; day <= 15; day++)
        {
            var id = AddOccurrence(new DateOnly(2025, 6, day), _userId, OccurrenceStatus.Completed);
            AddCompletionEvent(id, new DateTime(2025, 6, day, 15, 0, 0, DateTimeKind.Utc));
        }

        // Gap, then 2 earlier days (June 10-11).
        for (int day = 10; day <= 11; day++)
        {
            var id = AddOccurrence(new DateOnly(2025, 6, day), _userId, OccurrenceStatus.Completed);
            AddCompletionEvent(id, new DateTime(2025, 6, day, 15, 0, 0, DateTimeKind.Utc));
        }

        var streaks = await _svc.GetStreaksAsync(_householdId, TZ, null, CancellationToken.None);
        Assert.Equal(3, streaks.CurrentStreakDays);
        Assert.Equal(3, streaks.LongestStreakDays);
    }

    [Fact]
    public async Task Streaks_BrokenStreak_CurrentIsZero()
    {
        // Completed only on June 13 (2 days ago, no completion on June 14 or today).
        var id = AddOccurrence(new DateOnly(2025, 6, 13), _userId, OccurrenceStatus.Completed);
        AddCompletionEvent(id, new DateTime(2025, 6, 13, 15, 0, 0, DateTimeKind.Utc));

        var streaks = await _svc.GetStreaksAsync(_householdId, TZ, null, CancellationToken.None);
        Assert.Equal(0, streaks.CurrentStreakDays);
        Assert.Equal(1, streaks.LongestStreakDays);
    }

    [Fact]
    public async Task Streaks_YesterdayOnly_CurrentIsOne()
    {
        var id = AddOccurrence(new DateOnly(2025, 6, 14), _userId, OccurrenceStatus.Completed);
        AddCompletionEvent(id, new DateTime(2025, 6, 14, 15, 0, 0, DateTimeKind.Utc));

        var streaks = await _svc.GetStreaksAsync(_householdId, TZ, null, CancellationToken.None);
        Assert.Equal(1, streaks.CurrentStreakDays);
    }

    // ───────────────────── Today's wins ─────────────────────

    [Fact]
    public async Task Today_OnlyShowsTodaysCompletions()
    {
        // June 15 in ET: starts at June 15, 04:00 UTC; ends at June 16, 04:00 UTC.

        // Completed today at 10 AM ET = June 15, 14:00 UTC.
        var todayId = AddOccurrence(new DateOnly(2025, 6, 15), _userId, OccurrenceStatus.Completed);
        AddCompletionEvent(todayId, new DateTime(2025, 6, 15, 14, 0, 0, DateTimeKind.Utc));

        // Completed yesterday at 10 AM ET = June 14, 14:00 UTC.
        var yesterdayId = AddOccurrence(new DateOnly(2025, 6, 14), _userId, OccurrenceStatus.Completed);
        AddCompletionEvent(yesterdayId, new DateTime(2025, 6, 14, 14, 0, 0, DateTimeKind.Utc));

        var today = await _svc.GetTodayAsync(_householdId, TZ, null, CancellationToken.None);
        Assert.Equal(1, today.CompletedCountToday);
        Assert.Single(today.Completions);
        Assert.Equal("Test Chore", today.Completions[0].Title);
    }

    // ───────────────────── Scope filtering ─────────────────────

    [Fact]
    public async Task Summary_ScopeMe_OnlyMyCompletions()
    {
        var myId = AddOccurrence(new DateOnly(2025, 6, 15), _userId, OccurrenceStatus.Completed);
        AddCompletionEvent(myId, new DateTime(2025, 6, 15, 15, 0, 0, DateTimeKind.Utc));

        var partnerOccId = AddOccurrence(new DateOnly(2025, 6, 15), _partnerId, OccurrenceStatus.Completed);
        AddCompletionEvent(partnerOccId, new DateTime(2025, 6, 15, 15, 0, 0, DateTimeKind.Utc));

        // Me scope.
        var meSummary = await _svc.GetSummaryAsync(
            _householdId, TZ, new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 30),
            _userId, CancellationToken.None);
        Assert.Equal(1, meSummary.TotalCompleted);

        // Household scope.
        var allSummary = await _svc.GetSummaryAsync(
            _householdId, TZ, new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 30),
            null, CancellationToken.None);
        Assert.Equal(2, allSummary.TotalCompleted);
    }

    [Fact]
    public async Task Summary_ScopePartner_OnlyPartnerCompletions()
    {
        AddOccurrence(new DateOnly(2025, 6, 15), _userId, OccurrenceStatus.Completed);

        var partnerOccId = AddOccurrence(new DateOnly(2025, 6, 15), _partnerId, OccurrenceStatus.Completed);
        AddCompletionEvent(partnerOccId, new DateTime(2025, 6, 15, 15, 0, 0, DateTimeKind.Utc));

        var partnerSummary = await _svc.GetSummaryAsync(
            _householdId, TZ, new DateOnly(2025, 6, 1), new DateOnly(2025, 6, 30),
            _partnerId, CancellationToken.None);
        Assert.Equal(1, partnerSummary.TotalCompleted);
    }

    // ───────────────────── Badges ─────────────────────

    [Fact]
    public async Task Badges_TenCompletions_EarnsBadge()
    {
        for (int i = 1; i <= 10; i++)
        {
            var id = AddOccurrence(new DateOnly(2025, 6, i), _userId, OccurrenceStatus.Completed);
            AddCompletionEvent(id, new DateTime(2025, 6, i, 15, 0, 0, DateTimeKind.Utc));
        }

        var badges = await _svc.GetBadgesAsync(_householdId, TZ, null, CancellationToken.None);
        Assert.Contains(badges.EarnedBadges, b => b.Key == "completions_10");
        Assert.Contains(badges.ProgressBadges, b => b.Key == "completions_25" && b.Current == 10);
    }

    [Fact]
    public async Task Badges_ThreeDayStreak_EarnsBadge()
    {
        for (int day = 13; day <= 15; day++)
        {
            var id = AddOccurrence(new DateOnly(2025, 6, day), _userId, OccurrenceStatus.Completed);
            AddCompletionEvent(id, new DateTime(2025, 6, day, 15, 0, 0, DateTimeKind.Utc));
        }

        var badges = await _svc.GetBadgesAsync(_householdId, TZ, null, CancellationToken.None);
        Assert.Contains(badges.EarnedBadges, b => b.Key == "streak_3");
    }

    [Fact]
    public async Task Badges_NoCompletions_OnlyProgressBadges()
    {
        var badges = await _svc.GetBadgesAsync(_householdId, TZ, null, CancellationToken.None);
        Assert.Empty(badges.EarnedBadges);
        Assert.Contains(badges.ProgressBadges, b => b.Key == "completions_10");
        Assert.Contains(badges.ProgressBadges, b => b.Key == "streak_3");
        Assert.Contains(badges.ProgressBadges, b => b.Key == "perfect_week");
    }

    // ───────────────────── ComputeStreaks unit tests ─────────────────────

    [Fact]
    public void ComputeStreaks_EmptyList_ReturnsZeros()
    {
        var (current, longest) = AchievementService.ComputeStreaks([], new DateOnly(2025, 6, 15));
        Assert.Equal(0, current);
        Assert.Equal(0, longest);
    }

    [Fact]
    public void ComputeStreaks_SingleDateToday_ReturnsOneOne()
    {
        var today = new DateOnly(2025, 6, 15);
        var (current, longest) = AchievementService.ComputeStreaks([today], today);
        Assert.Equal(1, current);
        Assert.Equal(1, longest);
    }

    [Fact]
    public void ComputeStreaks_Gap_LongestIsLarger()
    {
        var today = new DateOnly(2025, 6, 15);
        // Current: June 15, 14 (2 days). Older: June 10, 9, 8, 7 (4 days).
        List<DateOnly> dates =
        [
            new(2025, 6, 15), new(2025, 6, 14),
            new(2025, 6, 10), new(2025, 6, 9), new(2025, 6, 8), new(2025, 6, 7)
        ];
        var (current, longest) = AchievementService.ComputeStreaks(dates, today);
        Assert.Equal(2, current);
        Assert.Equal(4, longest);
    }
}
