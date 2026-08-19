using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using DoTogether.Domain.Enums;

namespace DoTogether.Application.DTOs;

// ── Auth ──

public static class AuthValidation
{
    public const string PasswordPattern = @"^(?=.{10,}$)(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z0-9]).*$";
    public const string UsernamePattern = @"^[a-zA-Z0-9._-]{3,40}$";
}

public record RegisterDto(
    [Required, MinLength(3), MaxLength(40), RegularExpression(AuthValidation.UsernamePattern, ErrorMessage = "Username must be 3-40 characters and can only include letters, numbers, dots, underscores, and hyphens.")] string Username,
    [Required, MaxLength(120)] string DisplayName,
    [Required, MinLength(10), RegularExpression(AuthValidation.PasswordPattern, ErrorMessage = "Password must be at least 10 characters and include lowercase, uppercase, number, and symbol characters.")] string Password);

public record LoginDto(
    [Required] string Username,
    [Required] string Password);

public record RefreshRequestDto(string? RefreshToken);
public record AuthResponseDto(string AccessToken, DateTime AccessTokenExpiresAtUtc, string? RefreshToken, DateTime RefreshTokenExpiresAtUtc, UserDto User);

// ── User ──

public record UserDto(Guid Id, string Username, string DisplayName);

// ── Household ──

public record CreateHouseholdDto([Required, MaxLength(120)] string Name, [Required] string TimeZoneId);
public record UpdateHouseholdDto([Required, MaxLength(120)] string Name);
public record HouseholdDto(Guid Id, string Name, string TimeZoneId, List<HouseholdMemberDto> Members);
public record HouseholdMemberDto(Guid UserId, string DisplayName, string Username, MemberRole Role, DateTime JoinedAtUtc);
public record InviteResponseDto(Guid InviteId, string Token, DateTime ExpiresAtUtc);
public record JoinHouseholdDto([Required] string InviteToken);
public record UpdateHouseholdMemberRoleDto([Required] MemberRole Role);

// ── Chore Template ──

public record CreateChoreTemplateDto(
    [Required, MaxLength(200)] string Title,
    string? Description,
    [Required] RecurrenceRuleDto RecurrenceRule,
    Guid? AssigneeId,
    [Required] DateOnly StartDate,
    DateOnly? EndDate);

public sealed class UpdateChoreTemplateDto
{
    [MaxLength(200)]
    public string? Title { get; init; }

    private string? _description;
    public string? Description
    {
        get => _description;
        init
        {
            _description = value;
            DescriptionSpecified = true;
        }
    }

    public RecurrenceRuleDto? RecurrenceRule { get; init; }

    private Guid? _assigneeId;
    public Guid? AssigneeId
    {
        get => _assigneeId;
        init
        {
            _assigneeId = value;
            AssigneeIdSpecified = true;
        }
    }

    private DateOnly? _endDate;
    public DateOnly? EndDate
    {
        get => _endDate;
        init
        {
            _endDate = value;
            EndDateSpecified = true;
        }
    }

    public bool? IsActive { get; init; }

    [JsonIgnore]
    public bool DescriptionSpecified { get; private set; }

    [JsonIgnore]
    public bool AssigneeIdSpecified { get; private set; }

    [JsonIgnore]
    public bool EndDateSpecified { get; private set; }
}

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

// ── Recipes ──

public record RecipeImportWarningDto(string Code, string Message);

public record RecipeSourceDto(
    RecipeOriginType Type,
    string? Url,
    string? Domain,
    string? Attribution,
    bool RequiresManualReview,
    List<RecipeImportWarningDto> ImportWarnings);

public record RecipeSourceInputDto(
    RecipeOriginType Type,
    [Url] string? Url,
    [MaxLength(255)] string? Domain,
    [MaxLength(200)] string? Attribution,
    bool RequiresManualReview,
    List<RecipeImportWarningDto>? ImportWarnings);

public record RecipeNutritionDto(
    int? Calories,
    decimal? ProteinGrams,
    decimal? CarbsGrams,
    decimal? FatGrams);

public record RecipeIngredientDto(
    Guid Id,
    int SortOrder,
    string RawText,
    decimal? Quantity,
    string? Unit,
    string? Item,
    string? Note);

public record RecipeIngredientInputDto(
    [Required, MaxLength(400)] string RawText,
    decimal? Quantity,
    [MaxLength(60)] string? Unit,
    [MaxLength(200)] string? Item,
    [MaxLength(200)] string? Note);

public record RecipeInstructionDto(
    Guid Id,
    int SortOrder,
    string Text,
    string? Section);

public record RecipeInstructionInputDto(
    [Required, MaxLength(2000)] string Text,
    [MaxLength(120)] string? Section);

public record RecipeUpsertDto(
    [Required, MaxLength(200)] string Name,
    string? Description,
    RecipeSourceInputDto? Source,
    [Required] List<RecipeIngredientInputDto> Ingredients,
    [Required] List<RecipeInstructionInputDto> Instructions,
    int? Servings,
    [MaxLength(200)] string? YieldText,
    int? PrepMinutes,
    int? CookMinutes,
    int? TotalMinutes,
    RecipeNutritionDto? Nutrition,
    [Url] string? ImageUrl,
    List<string>? Tags,
    bool IsArchived = false);

public record RecipeSummaryDto(
    Guid Id,
    string Name,
    string? Description,
    RecipeOriginType OriginType,
    int IngredientCount,
    int InstructionCount,
    int? Servings,
    string? ImageUrl,
    bool IsArchived,
    DateTime UpdatedAtUtc,
    List<string> Tags);

public record RecipeDetailDto(
    Guid Id,
    Guid HouseholdId,
    string Name,
    string? Description,
    RecipeOriginType OriginType,
    RecipeSourceDto Source,
    List<RecipeIngredientDto> Ingredients,
    List<RecipeInstructionDto> Instructions,
    int? Servings,
    string? YieldText,
    int? PrepMinutes,
    int? CookMinutes,
    int? TotalMinutes,
    RecipeNutritionDto? Nutrition,
    string? ImageUrl,
    List<string> Tags,
    bool IsArchived,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public record RecipeImportRequestDto([Required, Url] string Url);

public record RecipeImportPreviewDto(
    string SourceUrl,
    string SourceDomain,
    ImportConfidenceLevel Confidence,
    bool RequiresManualReview,
    List<RecipeImportWarningDto> Warnings,
    RecipeUpsertDto Recipe);

// ── Meal Plans ──

public record DateRangeQueryDto
{
    [Required] public DateOnly From { get; init; }
    [Required] public DateOnly To { get; init; }
}

public record MealPlanEntryUpsertDto(
    [Required] DateOnly Date,
    [Required] MealSlot MealSlot,
    [Required] Guid RecipeId,
    [Range(0.1, 100)] decimal ServingsPlanned,
    [MaxLength(500)] string? Notes);

public record MealPlanRecipeSummaryDto(
    Guid RecipeId,
    string Name,
    string? ImageUrl,
    RecipeOriginType OriginType,
    bool IsArchived);

public record MealPlanEntryDto(
    Guid Id,
    Guid HouseholdId,
    DateOnly Date,
    MealSlot MealSlot,
    MealPlanRecipeSummaryDto Recipe,
    decimal ServingsPlanned,
    string? Notes,
    Guid CreatedByUserId,
    Guid? UpdatedByUserId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

// ── Grocery Lists ──

public record GroceryListItemSourceDto(
    DateOnly PlannedDate,
    MealSlot MealSlot,
    Guid RecipeId,
    string RecipeName,
    decimal ServingsMultiplier);

public record GroceryListItemDto(
    string DisplayText,
    decimal? Quantity,
    string? Unit,
    string Item,
    bool IsNormalized,
    List<GroceryListItemSourceDto> Sources);

public record GroceryListDto(
    DateOnly From,
    DateOnly To,
    List<GroceryListItemDto> Items,
    List<string> Warnings);

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
