namespace DoTogether.Domain.Entities;

public class RecipeInstructionStep
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid HouseholdRecipeId { get; set; }
    public HouseholdRecipe HouseholdRecipe { get; set; } = null!;

    public int SortOrder { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? Section { get; set; }
}