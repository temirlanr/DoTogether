using DoTogether.Domain.Enums;

namespace DoTogether.Domain.Entities;

public class MealPlanEntry : BaseEntity
{
    public Guid HouseholdId { get; set; }
    public Household Household { get; set; } = null!;

    public DateOnly Date { get; set; }
    public MealSlot MealSlot { get; set; }

    public Guid RecipeId { get; set; }
    public HouseholdRecipe Recipe { get; set; } = null!;

    public decimal ServingsPlanned { get; set; } = 1;
    public string? Notes { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;

    public Guid? UpdatedByUserId { get; set; }
    public User? UpdatedByUser { get; set; }
}