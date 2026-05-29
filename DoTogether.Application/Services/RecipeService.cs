using System.Text.Json;
using DoTogether.Application.DTOs;
using DoTogether.Application.Exceptions;
using DoTogether.Application.Interfaces;
using DoTogether.Domain.Entities;
using DoTogether.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Application.Services;

public class RecipeService(
    IAppDbContext db,
    HouseholdAccessService householdAccess,
    IDateTimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<List<RecipeSummaryDto>> ListAsync(
        Guid householdId,
        Guid userId,
        bool includeArchived,
        CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, userId, ct);

        var query = db.HouseholdRecipes
            .Include(r => r.Ingredients)
            .Include(r => r.Instructions)
            .Where(r => r.HouseholdId == householdId && !r.IsDeleted);

        if (!includeArchived)
            query = query.Where(r => !r.IsArchived);

        var recipes = await query
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

        return recipes.Select(MapSummary).ToList();
    }

    public async Task<RecipeDetailDto> GetAsync(Guid householdId, Guid recipeId, Guid userId, CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, userId, ct);
        var recipe = await LoadRecipeAsync(householdId, recipeId, ct);
        return MapDetail(recipe);
    }

    public async Task<RecipeDetailDto> CreateAsync(Guid householdId, Guid userId, RecipeUpsertDto dto, CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, userId, ct);
        ValidateRecipe(dto);

        var recipe = new HouseholdRecipe
        {
            HouseholdId = householdId,
            CreatedByUserId = userId,
            UpdatedByUserId = userId
        };

        ApplyRecipeMetadata(recipe, dto, userId);
        recipe.Ingredients = BuildIngredients(recipe, dto.Ingredients);
        recipe.Instructions = BuildInstructions(recipe, dto.Instructions);
        db.HouseholdRecipes.Add(recipe);
        await db.SaveChangesAsync(ct);

        return MapDetail(recipe);
    }

    public async Task<RecipeDetailDto> UpdateAsync(
        Guid householdId,
        Guid recipeId,
        Guid userId,
        RecipeUpsertDto dto,
        CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, userId, ct);
        ValidateRecipe(dto);

        var recipe = await LoadRecipeAsync(householdId, recipeId, ct);

        db.RecipeIngredients.RemoveRange(recipe.Ingredients.ToList());
        db.RecipeInstructionSteps.RemoveRange(recipe.Instructions.ToList());
        recipe.Ingredients.Clear();
        recipe.Instructions.Clear();

        ApplyRecipeMetadata(recipe, dto, userId);

        var newIngredients = BuildIngredients(recipe, dto.Ingredients);
        var newInstructions = BuildInstructions(recipe, dto.Instructions);

        recipe.Ingredients = newIngredients;
        recipe.Instructions = newInstructions;
        db.RecipeIngredients.AddRange(newIngredients);
        db.RecipeInstructionSteps.AddRange(newInstructions);
        await db.SaveChangesAsync(ct);

        return MapDetail(recipe);
    }

    public async Task ArchiveAsync(Guid householdId, Guid recipeId, Guid userId, CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, userId, ct);

        var recipe = await db.HouseholdRecipes
            .FirstOrDefaultAsync(r => r.Id == recipeId && r.HouseholdId == householdId && !r.IsDeleted, ct)
            ?? throw ApiProblemException.NotFound("recipe_not_found", "Recipe not found.");

        recipe.IsArchived = true;
        recipe.UpdatedAtUtc = clock.UtcNow;
        recipe.UpdatedByUserId = userId;
        await db.SaveChangesAsync(ct);
    }

    private async Task<HouseholdRecipe> LoadRecipeAsync(Guid householdId, Guid recipeId, CancellationToken ct)
    {
        return await db.HouseholdRecipes
            .Include(r => r.Ingredients)
            .Include(r => r.Instructions)
            .FirstOrDefaultAsync(r => r.Id == recipeId && r.HouseholdId == householdId && !r.IsDeleted, ct)
            ?? throw ApiProblemException.NotFound("recipe_not_found", "Recipe not found.");
    }

    private void ApplyRecipeMetadata(HouseholdRecipe recipe, RecipeUpsertDto dto, Guid userId)
    {
        var source = NormalizeSource(dto.Source);
        var normalizedTags = NormalizeTags(dto.Tags);

        recipe.Name = dto.Name.Trim();
        recipe.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        recipe.OriginType = source.Type;
        recipe.SourceUrl = source.Url;
        recipe.SourceDomain = source.Domain ?? TryGetDomain(source.Url);
        recipe.SourceAttribution = string.IsNullOrWhiteSpace(source.Attribution) ? null : source.Attribution.Trim();
        recipe.SourceRequiresManualReview = source.RequiresManualReview;
        recipe.ImportWarningsJson = SerializeWarnings(source.ImportWarnings);
        recipe.Servings = dto.Servings;
        recipe.YieldText = string.IsNullOrWhiteSpace(dto.YieldText) ? null : dto.YieldText.Trim();
        recipe.PrepMinutes = dto.PrepMinutes;
        recipe.CookMinutes = dto.CookMinutes;
        recipe.TotalMinutes = dto.TotalMinutes ?? SumOptionalMinutes(dto.PrepMinutes, dto.CookMinutes);
        recipe.CaloriesKcal = dto.Nutrition?.Calories;
        recipe.ProteinGrams = dto.Nutrition?.ProteinGrams;
        recipe.CarbsGrams = dto.Nutrition?.CarbsGrams;
        recipe.FatGrams = dto.Nutrition?.FatGrams;
        recipe.ImageUrl = string.IsNullOrWhiteSpace(dto.ImageUrl) ? null : dto.ImageUrl.Trim();
        recipe.TagsJson = SerializeTags(normalizedTags);
        recipe.IsArchived = dto.IsArchived;
        recipe.UpdatedAtUtc = clock.UtcNow;
        recipe.UpdatedByUserId = userId;
    }

    private static void ValidateRecipe(RecipeUpsertDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            throw ApiProblemException.BadRequest("recipe_name_required", "Recipe name is required.");

        if (dto.Ingredients.Count == 0)
            throw ApiProblemException.BadRequest(
                "recipe_ingredients_required",
                "At least one ingredient is required to save a recipe.");

        if (dto.Instructions.Count == 0)
            throw ApiProblemException.BadRequest(
                "recipe_instructions_required",
                "At least one instruction step is required to save a recipe.");

        if (dto.Ingredients.Any(i => string.IsNullOrWhiteSpace(i.RawText)))
            throw ApiProblemException.BadRequest(
                "recipe_ingredient_text_required",
                "Each ingredient must include a human-readable ingredient line.");

        if (dto.Instructions.Any(i => string.IsNullOrWhiteSpace(i.Text)))
            throw ApiProblemException.BadRequest(
                "recipe_instruction_text_required",
                "Each instruction step must include step text.");

        if (dto.Servings is < 1)
            throw ApiProblemException.BadRequest(
                "recipe_servings_invalid",
                "Servings must be at least 1 when provided.");

        if (dto.PrepMinutes is < 0 || dto.CookMinutes is < 0 || dto.TotalMinutes is < 0)
            throw ApiProblemException.BadRequest(
                "recipe_time_invalid",
                "Recipe time values cannot be negative.");

        var source = dto.Source;
        if (source is not null && source.Type == RecipeOriginType.ImportedStructuredData && string.IsNullOrWhiteSpace(source.Url))
            throw ApiProblemException.BadRequest(
                "recipe_source_url_required",
                "Imported recipes must include the original source URL.");
    }

    private static RecipeSourceInputDto NormalizeSource(RecipeSourceInputDto? source)
    {
        if (source is null)
            return new RecipeSourceInputDto(RecipeOriginType.Custom, null, null, null, false, null);

        return source with
        {
            Url = string.IsNullOrWhiteSpace(source.Url) ? null : source.Url.Trim(),
            Domain = string.IsNullOrWhiteSpace(source.Domain) ? null : source.Domain.Trim().ToLowerInvariant(),
            Attribution = string.IsNullOrWhiteSpace(source.Attribution) ? null : source.Attribution.Trim(),
            ImportWarnings = source.ImportWarnings?.Where(w => !string.IsNullOrWhiteSpace(w.Code) && !string.IsNullOrWhiteSpace(w.Message)).ToList()
        };
    }

    private static List<RecipeIngredient> BuildIngredients(HouseholdRecipe recipe, IEnumerable<RecipeIngredientInputDto> inputs)
    {
        return inputs
            .Select((input, index) => new RecipeIngredient
            {
                HouseholdRecipe = recipe,
                SortOrder = index,
                RawText = input.RawText.Trim(),
                Quantity = input.Quantity,
                Unit = string.IsNullOrWhiteSpace(input.Unit) ? null : input.Unit.Trim(),
                Item = string.IsNullOrWhiteSpace(input.Item) ? null : input.Item.Trim(),
                Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim()
            })
            .ToList();
    }

    private static List<RecipeInstructionStep> BuildInstructions(HouseholdRecipe recipe, IEnumerable<RecipeInstructionInputDto> inputs)
    {
        return inputs
            .Select((input, index) => new RecipeInstructionStep
            {
                HouseholdRecipe = recipe,
                SortOrder = index,
                Text = input.Text.Trim(),
                Section = string.IsNullOrWhiteSpace(input.Section) ? null : input.Section.Trim()
            })
            .ToList();
    }

    private static RecipeSummaryDto MapSummary(HouseholdRecipe recipe) => new(
        recipe.Id,
        recipe.Name,
        recipe.Description,
        recipe.OriginType,
        recipe.Ingredients.Count,
        recipe.Instructions.Count,
        recipe.Servings,
        recipe.ImageUrl,
        recipe.IsArchived,
        recipe.UpdatedAtUtc,
        DeserializeTags(recipe.TagsJson));

    private static RecipeDetailDto MapDetail(HouseholdRecipe recipe) => new(
        recipe.Id,
        recipe.HouseholdId,
        recipe.Name,
        recipe.Description,
        recipe.OriginType,
        MapSource(recipe),
        recipe.Ingredients
            .OrderBy(i => i.SortOrder)
            .Select(i => new RecipeIngredientDto(i.Id, i.SortOrder, i.RawText, i.Quantity, i.Unit, i.Item, i.Note))
            .ToList(),
        recipe.Instructions
            .OrderBy(i => i.SortOrder)
            .Select(i => new RecipeInstructionDto(i.Id, i.SortOrder, i.Text, i.Section))
            .ToList(),
        recipe.Servings,
        recipe.YieldText,
        recipe.PrepMinutes,
        recipe.CookMinutes,
        recipe.TotalMinutes,
        MapNutrition(recipe),
        recipe.ImageUrl,
        DeserializeTags(recipe.TagsJson),
        recipe.IsArchived,
        recipe.CreatedAtUtc,
        recipe.UpdatedAtUtc);

    private static RecipeSourceDto MapSource(HouseholdRecipe recipe) => new(
        recipe.OriginType,
        recipe.SourceUrl,
        recipe.SourceDomain,
        recipe.SourceAttribution,
        recipe.SourceRequiresManualReview,
        DeserializeWarnings(recipe.ImportWarningsJson));

    private static RecipeNutritionDto? MapNutrition(HouseholdRecipe recipe)
    {
        if (recipe.CaloriesKcal is null && recipe.ProteinGrams is null && recipe.CarbsGrams is null && recipe.FatGrams is null)
            return null;

        return new RecipeNutritionDto(recipe.CaloriesKcal, recipe.ProteinGrams, recipe.CarbsGrams, recipe.FatGrams);
    }

    private static int? SumOptionalMinutes(int? prepMinutes, int? cookMinutes)
    {
        if (prepMinutes is null && cookMinutes is null)
            return null;

        return (prepMinutes ?? 0) + (cookMinutes ?? 0);
    }

    private static string? TryGetDomain(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host.ToLowerInvariant()
            : null;
    }

    private static string? SerializeWarnings(List<RecipeImportWarningDto>? warnings)
        => warnings is { Count: > 0 } ? JsonSerializer.Serialize(warnings, JsonOptions) : null;

    private static List<RecipeImportWarningDto> DeserializeWarnings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<RecipeImportWarningDto>>(json, JsonOptions) ?? [];
    }

    private static string? SerializeTags(List<string> tags)
        => tags.Count > 0 ? JsonSerializer.Serialize(tags, JsonOptions) : null;

    private static List<string> DeserializeTags(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
    }

    private static List<string> NormalizeTags(IEnumerable<string>? tags)
    {
        if (tags is null)
            return [];

        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            var trimmed = tag.Trim();
            if (seen.Add(trimmed))
                normalized.Add(trimmed);
        }

        return normalized;
    }
}