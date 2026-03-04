using System.ComponentModel.DataAnnotations;
using DoTogether.Domain.Enums;

namespace DoTogether.Application.DTOs;

// ── Auth ──

public record RegisterDto(
    [Required, EmailAddress] string Email,
    [Required, MaxLength(120)] string DisplayName,
    [Required, MinLength(8)] string Password);

public record LoginDto(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public record RefreshRequestDto([Required] string RefreshToken);
public record AuthResponseDto(string AccessToken, string RefreshToken, DateTime ExpiresAtUtc, UserDto User);

// ── User ──

public record UserDto(Guid Id, string Email, string DisplayName);

// ── Household ──

public record CreateHouseholdDto([Required, MaxLength(120)] string Name, [Required] string TimeZoneId);
public record HouseholdDto(Guid Id, string Name, string TimeZoneId, List<HouseholdMemberDto> Members);
public record HouseholdMemberDto(Guid UserId, string DisplayName, string Email, MemberRole Role, DateTime JoinedAtUtc);
public record InviteMemberDto([Required, EmailAddress] string Email);
public record InviteResponseDto(Guid InviteId, string Token, DateTime ExpiresAtUtc);
public record JoinHouseholdDto([Required] string InviteToken);

// ── Chore Template ──

public record CreateChoreTemplateDto(
    [Required, MaxLength(200)] string Title,
    string? Description,
    [Required] RecurrenceRuleDto RecurrenceRule,
    Guid? AssigneeId,
    [Required] DateOnly StartDate,
    DateOnly? EndDate);

public record UpdateChoreTemplateDto(
    [MaxLength(200)] string? Title,
    string? Description,
    RecurrenceRuleDto? RecurrenceRule,
    Guid? AssigneeId,
    DateOnly? EndDate,
    bool? IsActive);

public record RecurrenceRuleDto(
    RecurrenceType Type,
    int Interval = 1,
    List<int>? DaysOfWeek = null,
    int? DayOfMonth = null);

public record ChoreTemplateDto(
    Guid Id,
    string Title,
    string? Description,
    RecurrenceRuleDto RecurrenceRule,
    Guid? AssigneeId,
    string? AssigneeName,
    DateOnly StartDate,
    DateOnly? EndDate,
    bool IsActive,
    DateTime CreatedAtUtc);

// ── Chore Occurrence ──

public record ChoreOccurrenceDto(
    Guid Id,
    Guid ChoreTemplateId,
    string ChoreTitle,
    Guid? AssigneeId,
    string? AssigneeName,
    DateOnly DueDate,
    OccurrenceStatus Status,
    int Version,
    List<ChoreEventDto> Events);

public record ChoreEventDto(
    Guid Id,
    ChoreEventType EventType,
    Guid PerformedByUserId,
    string PerformedByName,
    DateTime OccurredAtUtc,
    Guid ClientOperationId,
    string? Metadata);

// ── Mutation (idempotent) ──

public record MutationRequestDto([Required] Guid ClientOperationId);
public record ReassignRequestDto([Required] Guid ClientOperationId, [Required] Guid NewAssigneeId);

// ── Calendar ──

public record CalendarQueryDto
{
    [Required] public DateOnly From { get; init; }
    [Required] public DateOnly To { get; init; }
    public Guid? AssigneeId { get; init; }
}

public record DayAggregateDto(
    DateOnly Date,
    int Due,
    int Done,
    int Missed,
    int Skipped);

// ── Device ──

public record RegisterDeviceDto([Required] PushPlatform Platform, [Required] string Token);

// ── Achievements ──

public record AchievementSummaryQueryDto
{
    public AchievementScope Scope { get; init; } = AchievementScope.Household;
    [Required] public DateOnly From { get; init; }
    [Required] public DateOnly To { get; init; }
}

public record AchievementScopeQueryDto
{
    public AchievementScope Scope { get; init; } = AchievementScope.Household;
}

public record AchievementSummaryDto(
    int TotalCompleted,
    int TotalScheduled,
    double CompletionRate,
    List<CompletedByDayDto> CompletedByDay,
    List<TopTemplateDto> TopTemplates,
    int OnTimeCompleted,
    int LateCompleted);

public record CompletedByDayDto(DateOnly Date, int Count);

public record TopTemplateDto(Guid TemplateId, string Title, int CompletedCount);

public record TodayWinsDto(
    int CompletedCountToday,
    List<TodayCompletionDto> Completions);

public record TodayCompletionDto(
    Guid OccurrenceId,
    string Title,
    DateTime CompletedAtUtc,
    DateOnly ScheduledDate,
    bool WasLate);

public record StreaksDto(
    int CurrentStreakDays,
    int LongestStreakDays,
    int CurrentOnTimeStreakDays);

public record BadgesResponseDto(
    List<BadgeDto> EarnedBadges,
    List<BadgeProgressDto> ProgressBadges);

public record BadgeDto(string Key, string Title, string Description);

public record BadgeProgressDto(string Key, string Title, string Description, int Current, int Target);
