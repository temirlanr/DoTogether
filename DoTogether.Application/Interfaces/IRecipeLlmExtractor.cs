using DoTogether.Application.DTOs;

namespace DoTogether.Application.Interfaces;

public interface IRecipeLlmExtractor
{
    Task<RecipeLlmExtractionResult?> ExtractAsync(string pageText, Uri sourceUri, CancellationToken ct);
}

public sealed record RecipeLlmExtractionResult(
    string Name,
    string? Description,
    List<string> Ingredients,
    List<string> Instructions,
    int? Servings,
    string? YieldText,
    int? PrepMinutes,
    int? CookMinutes,
    int? TotalMinutes,
    RecipeNutritionDto? Nutrition,
    string? ImageUrl,
    List<string>? Tags,
    string? Attribution);