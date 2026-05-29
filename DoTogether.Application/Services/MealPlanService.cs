using System.Globalization;
using DoTogether.Application.DTOs;
using DoTogether.Application.Exceptions;
using DoTogether.Application.Interfaces;
using DoTogether.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Application.Services;

public class MealPlanService(
    IAppDbContext db,
    HouseholdAccessService householdAccess,
    IDateTimeProvider clock)
{
    public async Task<List<MealPlanEntryDto>> ListAsync(
        Guid householdId,
        Guid userId,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, userId, ct);
        ValidateRange(from, to);

        var entries = await db.MealPlanEntries
            .Include(m => m.Recipe)
            .Where(m => m.HouseholdId == householdId && m.Date >= from && m.Date <= to && !m.IsDeleted)
            .OrderBy(m => m.Date)
            .ThenBy(m => m.MealSlot)
            .ToListAsync(ct);

        return entries.Select(MapEntry).ToList();
    }

    public async Task<MealPlanEntryDto> CreateAsync(
        Guid householdId,
        Guid userId,
        MealPlanEntryUpsertDto dto,
        CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, userId, ct);
        ValidateEntry(dto);

        var recipe = await LoadRecipeForPlanningAsync(householdId, dto.RecipeId, ct);
        await EnsureSlotAvailableAsync(householdId, dto.Date, dto.MealSlot, null, ct);

        var entry = new MealPlanEntry
        {
            HouseholdId = householdId,
            Date = dto.Date,
            MealSlot = dto.MealSlot,
            RecipeId = recipe.Id,
            Recipe = recipe,
            ServingsPlanned = dto.ServingsPlanned,
            Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim(),
            CreatedByUserId = userId,
            UpdatedByUserId = userId
        };

        db.MealPlanEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        return MapEntry(entry);
    }

    public async Task<MealPlanEntryDto> UpdateAsync(
        Guid householdId,
        Guid entryId,
        Guid userId,
        MealPlanEntryUpsertDto dto,
        CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, userId, ct);
        ValidateEntry(dto);

        var entry = await db.MealPlanEntries
            .Include(m => m.Recipe)
            .FirstOrDefaultAsync(m => m.Id == entryId && m.HouseholdId == householdId && !m.IsDeleted, ct)
            ?? throw ApiProblemException.NotFound("meal_plan_not_found", "Meal plan entry not found.");

        var recipe = await LoadRecipeForPlanningAsync(householdId, dto.RecipeId, ct);
        await EnsureSlotAvailableAsync(householdId, dto.Date, dto.MealSlot, entryId, ct);

        entry.Date = dto.Date;
        entry.MealSlot = dto.MealSlot;
        entry.RecipeId = recipe.Id;
        entry.Recipe = recipe;
        entry.ServingsPlanned = dto.ServingsPlanned;
        entry.Notes = string.IsNullOrWhiteSpace(dto.Notes) ? null : dto.Notes.Trim();
        entry.UpdatedAtUtc = clock.UtcNow;
        entry.UpdatedByUserId = userId;

        await db.SaveChangesAsync(ct);
        return MapEntry(entry);
    }

    public async Task DeleteAsync(Guid householdId, Guid entryId, Guid userId, CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, userId, ct);

        var entry = await db.MealPlanEntries
            .FirstOrDefaultAsync(m => m.Id == entryId && m.HouseholdId == householdId && !m.IsDeleted, ct)
            ?? throw ApiProblemException.NotFound("meal_plan_not_found", "Meal plan entry not found.");

        entry.IsDeleted = true;
        entry.UpdatedAtUtc = clock.UtcNow;
        entry.UpdatedByUserId = userId;
        await db.SaveChangesAsync(ct);
    }

    public async Task<GroceryListDto> GenerateGroceryListAsync(
        Guid householdId,
        Guid userId,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        await householdAccess.EnsureMemberAsync(householdId, userId, ct);
        ValidateRange(from, to);

        var entries = await db.MealPlanEntries
            .Include(m => m.Recipe)
                .ThenInclude(r => r.Ingredients)
            .Where(m => m.HouseholdId == householdId && m.Date >= from && m.Date <= to && !m.IsDeleted)
            .OrderBy(m => m.Date)
            .ThenBy(m => m.MealSlot)
            .ToListAsync(ct);

        var warnings = new List<string>();
        var normalized = new Dictionary<string, AggregatedGroceryItem>(StringComparer.OrdinalIgnoreCase);
        var fallback = new Dictionary<string, AggregatedGroceryItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var multiplier = CalculateServingsMultiplier(entry, warnings);

            foreach (var ingredient in entry.Recipe.Ingredients.OrderBy(i => i.SortOrder))
            {
                var source = new GroceryListItemSourceDto(
                    entry.Date,
                    entry.MealSlot,
                    entry.RecipeId,
                    entry.Recipe.Name,
                    multiplier);

                if (ingredient.Quantity is { } quantity && !string.IsNullOrWhiteSpace(ingredient.Item))
                {
                    var unit = NormalizeUnit(ingredient.Unit);
                    var item = NormalizeItemName(ingredient.Item);
                    var key = $"{item}|{unit}";

                    if (!normalized.TryGetValue(key, out var bucket))
                    {
                        bucket = new AggregatedGroceryItem(item, unit, true, ingredient.RawText);
                        normalized[key] = bucket;
                    }

                    bucket.Quantity += quantity * multiplier;
                    bucket.Sources.Add(source);
                    continue;
                }

                var fallbackText = ingredient.RawText.Trim();
                var fallbackKey = NormalizeItemName(fallbackText);
                if (!fallback.TryGetValue(fallbackKey, out var fallbackBucket))
                {
                    fallbackBucket = new AggregatedGroceryItem(
                        ingredient.Item?.Trim() ?? fallbackText,
                        null,
                        false,
                        fallbackText);
                    fallback[fallbackKey] = fallbackBucket;
                }

                fallbackBucket.Sources.Add(source);
            }
        }

        if (fallback.Count > 0)
        {
            warnings.Add(
                "Some ingredients could not be quantity-normalized and were preserved as human-readable lines.");
        }

        var items = normalized.Values
            .OrderBy(i => i.Item, StringComparer.OrdinalIgnoreCase)
            .Select(i => new GroceryListItemDto(
                FormatDisplayText(i.Quantity, i.Unit, i.Item),
                i.Quantity,
                i.Unit,
                i.Item,
                true,
                i.Sources))
            .Concat(
                fallback.Values
                    .OrderBy(i => i.DisplayText, StringComparer.OrdinalIgnoreCase)
                    .Select(i => new GroceryListItemDto(
                        i.DisplayText,
                        null,
                        null,
                        i.Item,
                        false,
                        i.Sources)))
            .ToList();

        return new GroceryListDto(from, to, items, warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private async Task<HouseholdRecipe> LoadRecipeForPlanningAsync(Guid householdId, Guid recipeId, CancellationToken ct)
    {
        var recipe = await db.HouseholdRecipes
            .Include(r => r.Ingredients)
            .FirstOrDefaultAsync(r => r.Id == recipeId && r.HouseholdId == householdId && !r.IsDeleted, ct)
            ?? throw ApiProblemException.NotFound("recipe_not_found", "Recipe not found.");

        if (recipe.IsArchived)
        {
            throw ApiProblemException.BadRequest(
                "recipe_archived",
                "Archived recipes cannot be used in meal plans.");
        }

        return recipe;
    }

    private async Task EnsureSlotAvailableAsync(
        Guid householdId,
        DateOnly date,
        DoTogether.Domain.Enums.MealSlot mealSlot,
        Guid? currentEntryId,
        CancellationToken ct)
    {
        var hasConflict = await db.MealPlanEntries.AnyAsync(
            m => m.HouseholdId == householdId
                 && m.Date == date
                 && m.MealSlot == mealSlot
                 && m.Id != currentEntryId
                 && !m.IsDeleted,
            ct);

        if (hasConflict)
        {
            throw ApiProblemException.Conflict(
                "meal_plan_slot_taken",
                "This meal slot already has a planned recipe for the selected date.");
        }
    }

    private static void ValidateRange(DateOnly from, DateOnly to)
    {
        if (from > to)
        {
            throw ApiProblemException.BadRequest(
                "date_range_invalid",
                "The 'from' date must be on or before the 'to' date.");
        }
    }

    private static void ValidateEntry(MealPlanEntryUpsertDto dto)
    {
        if (!Enum.IsDefined(dto.MealSlot))
        {
            throw ApiProblemException.BadRequest(
                "invalid_meal_slot",
                "Meal slot is invalid.");
        }

        if (dto.ServingsPlanned <= 0)
        {
            throw ApiProblemException.BadRequest(
                "servings_planned_invalid",
                "Planned servings must be greater than zero.");
        }
    }

    private static MealPlanEntryDto MapEntry(MealPlanEntry entry) => new(
        entry.Id,
        entry.HouseholdId,
        entry.Date,
        entry.MealSlot,
        new MealPlanRecipeSummaryDto(
            entry.RecipeId,
            entry.Recipe.Name,
            entry.Recipe.ImageUrl,
            entry.Recipe.OriginType,
            entry.Recipe.IsArchived),
        entry.ServingsPlanned,
        entry.Notes,
        entry.CreatedByUserId,
        entry.UpdatedByUserId,
        entry.CreatedAtUtc,
        entry.UpdatedAtUtc);

    private static decimal CalculateServingsMultiplier(MealPlanEntry entry, List<string> warnings)
    {
        if (entry.Recipe.Servings is > 0)
            return entry.ServingsPlanned / entry.Recipe.Servings.Value;

        warnings.Add($"Recipe '{entry.Recipe.Name}' has no servings metadata; grocery quantities use the recipe's base ingredient amounts.");
        return 1m;
    }

    private static string NormalizeUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return string.Empty;

        return unit.Trim().ToLowerInvariant() switch
        {
            "teaspoons" => "teaspoon",
            "tsp." => "tsp",
            "tablespoons" => "tablespoon",
            "tbsp." => "tbsp",
            "cups" => "cup",
            "ounces" => "ounce",
            "lbs" => "lb",
            _ => unit.Trim().ToLowerInvariant()
        };
    }

    private static string NormalizeItemName(string value)
        => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim().ToLowerInvariant();

    private static string FormatDisplayText(decimal quantity, string? unit, string item)
    {
        var qtyText = quantity.ToString(quantity % 1 == 0 ? "0" : "0.##", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(unit)
            ? $"{qtyText} {item}"
            : $"{qtyText} {unit} {item}";
    }

    private sealed class AggregatedGroceryItem(string item, string? unit, bool isNormalized, string displayText)
    {
        public string Item { get; } = item;
        public string? Unit { get; } = unit;
        public bool IsNormalized { get; } = isNormalized;
        public string DisplayText { get; } = displayText;
        public decimal Quantity { get; set; }
        public List<GroceryListItemSourceDto> Sources { get; } = [];
    }
}