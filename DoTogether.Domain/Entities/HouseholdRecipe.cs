using DoTogether.Domain.Enums;

namespace DoTogether.Domain.Entities;

public class HouseholdRecipe : BaseEntity
{
    public Guid HouseholdId { get; set; }
    public Household Household { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public RecipeOriginType OriginType { get; set; } = RecipeOriginType.Custom;
    public string? SourceUrl { get; set; }
    public string? SourceDomain { get; set; }
    public string? SourceAttribution { get; set; }
    public bool SourceRequiresManualReview { get; set; }
    public string? ImportWarningsJson { get; set; }

    public int? Servings { get; set; }
    public string? YieldText { get; set; }
    public int? PrepMinutes { get; set; }
    public int? CookMinutes { get; set; }
    public int? TotalMinutes { get; set; }

    public int? CaloriesKcal { get; set; }
    public decimal? ProteinGrams { get; set; }
    public decimal? CarbsGrams { get; set; }
    public decimal? FatGrams { get; set; }

    public string? ImageUrl { get; set; }
    public string? TagsJson { get; set; }
    public bool IsArchived { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;

    public Guid? UpdatedByUserId { get; set; }
    public User? UpdatedByUser { get; set; }

    public ICollection<RecipeIngredient> Ingredients { get; set; } = [];
    public ICollection<RecipeInstructionStep> Instructions { get; set; } = [];
    public ICollection<MealPlanEntry> MealPlanEntries { get; set; } = [];
}