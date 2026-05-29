namespace DoTogether.Domain.Entities;

public class RecipeIngredient
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid HouseholdRecipeId { get; set; }
    public HouseholdRecipe HouseholdRecipe { get; set; } = null!;

    public int SortOrder { get; set; }
    public string RawText { get; set; } = string.Empty;
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public string? Item { get; set; }
    public string? Note { get; set; }
}