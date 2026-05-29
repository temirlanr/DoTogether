using DoTogether.Application.DTOs;
using DoTogether.Application.Exceptions;
using DoTogether.Application.Services;
using DoTogether.Domain.Entities;
using DoTogether.Domain.Enums;
using DoTogether.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Tests;

public class MealPlanningServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly RecipeService _recipeService;
    private readonly MealPlanService _mealPlanService;

    private readonly Guid _householdId = Guid.NewGuid();
    private readonly Guid _otherHouseholdId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();
    private readonly Guid _outsiderId = Guid.NewGuid();

    public MealPlanningServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        var clock = new FakeDateTimeProvider(new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc));
        var householdAccess = new HouseholdAccessService(_db);
        _recipeService = new RecipeService(_db, householdAccess, clock);
        _mealPlanService = new MealPlanService(_db, householdAccess, clock);

        SeedBaseData();
    }

    [Fact]
    public async Task RecipeCrud_RespectsHouseholdScopingAndArchiving()
    {
        var recipe = await _recipeService.CreateAsync(_householdId, _userId, BuildRecipeDto("Weeknight Rice"), CancellationToken.None);
        var otherRecipe = await _recipeService.CreateAsync(_otherHouseholdId, _otherUserId, BuildRecipeDto("Other Home Soup"), CancellationToken.None);

        var updated = await _recipeService.UpdateAsync(
            _householdId,
            recipe.Id,
            _userId,
            BuildRecipeDto("Weeknight Rice Bowl", servings: 4),
            CancellationToken.None);

        Assert.Equal("Weeknight Rice Bowl", updated.Name);
        Assert.Equal(4, updated.Servings);

        var list = await _recipeService.ListAsync(_householdId, _userId, includeArchived: false, CancellationToken.None);
        Assert.Single(list);
        Assert.Equal(recipe.Id, list[0].Id);

        var ex = await Assert.ThrowsAsync<ApiProblemException>(() =>
            _recipeService.GetAsync(_householdId, otherRecipe.Id, _userId, CancellationToken.None));

        Assert.Equal(404, ex.StatusCode);
        Assert.Equal("recipe_not_found", ex.ErrorCode);

        await _recipeService.ArchiveAsync(_householdId, recipe.Id, _userId, CancellationToken.None);

        var activeList = await _recipeService.ListAsync(_householdId, _userId, includeArchived: false, CancellationToken.None);
        var archivedList = await _recipeService.ListAsync(_householdId, _userId, includeArchived: true, CancellationToken.None);

        Assert.Empty(activeList);
        Assert.Single(archivedList);
        Assert.True((await _recipeService.GetAsync(_householdId, recipe.Id, _userId, CancellationToken.None)).IsArchived);
    }

    [Fact]
    public async Task HouseholdAccess_IsEnforcedAcrossRecipeAndMealPlanServices()
    {
        var recipeError = await Assert.ThrowsAsync<ApiProblemException>(() =>
            _recipeService.CreateAsync(_householdId, _outsiderId, BuildRecipeDto("Blocked"), CancellationToken.None));

        Assert.Equal(403, recipeError.StatusCode);
        Assert.Equal("household_access_denied", recipeError.ErrorCode);

        var memberRecipe = await _recipeService.CreateAsync(_householdId, _userId, BuildRecipeDto("Allowed"), CancellationToken.None);

        var mealPlanError = await Assert.ThrowsAsync<ApiProblemException>(() =>
            _mealPlanService.CreateAsync(
                _householdId,
                _outsiderId,
                new MealPlanEntryUpsertDto(new DateOnly(2025, 6, 16), MealSlot.Dinner, memberRecipe.Id, 2, null),
                CancellationToken.None));

        Assert.Equal(403, mealPlanError.StatusCode);
        Assert.Equal("household_access_denied", mealPlanError.ErrorCode);
    }

    [Fact]
    public async Task MealPlanCrud_RespectsRecipeHouseholdAndSupportsGroceryAggregation()
    {
        var householdRecipe = await _recipeService.CreateAsync(_householdId, _userId, BuildRecipeDto("Family Dinner"), CancellationToken.None);
        var otherHouseholdRecipe = await _recipeService.CreateAsync(_otherHouseholdId, _otherUserId, BuildRecipeDto("Other Dinner"), CancellationToken.None);

        var crossHouseholdError = await Assert.ThrowsAsync<ApiProblemException>(() =>
            _mealPlanService.CreateAsync(
                _householdId,
                _userId,
                new MealPlanEntryUpsertDto(new DateOnly(2025, 6, 16), MealSlot.Dinner, otherHouseholdRecipe.Id, 4, null),
                CancellationToken.None));

        Assert.Equal(404, crossHouseholdError.StatusCode);
        Assert.Equal("recipe_not_found", crossHouseholdError.ErrorCode);

        var created = await _mealPlanService.CreateAsync(
            _householdId,
            _userId,
            new MealPlanEntryUpsertDto(new DateOnly(2025, 6, 16), MealSlot.Dinner, householdRecipe.Id, 4, "Prep ahead"),
            CancellationToken.None);

        var updated = await _mealPlanService.UpdateAsync(
            _householdId,
            created.Id,
            _userId,
            new MealPlanEntryUpsertDto(new DateOnly(2025, 6, 16), MealSlot.Lunch, householdRecipe.Id, 4, "Moved to lunch"),
            CancellationToken.None);

        Assert.Equal(MealSlot.Lunch, updated.MealSlot);
        Assert.Equal("Moved to lunch", updated.Notes);

        var entries = await _mealPlanService.ListAsync(
            _householdId,
            _userId,
            new DateOnly(2025, 6, 15),
            new DateOnly(2025, 6, 17),
            CancellationToken.None);

        Assert.Single(entries);
        Assert.Equal(updated.Id, entries[0].Id);

        var groceryList = await _mealPlanService.GenerateGroceryListAsync(
            _householdId,
            _userId,
            new DateOnly(2025, 6, 15),
            new DateOnly(2025, 6, 17),
            CancellationToken.None);

        Assert.Equal(3, groceryList.Items.Count);
        Assert.Contains(groceryList.Items, i => i.IsNormalized && i.Item == "rice" && i.Quantity == 4m && i.Unit == "cup");
        Assert.Contains(groceryList.Items, i => i.IsNormalized && i.Item == "onion" && i.Quantity == 2m);
        Assert.Contains(groceryList.Items, i => !i.IsNormalized && i.DisplayText == "salt to taste");
        Assert.Contains(groceryList.Warnings, warning => warning.Contains("could not be quantity-normalized", StringComparison.OrdinalIgnoreCase));

        await _mealPlanService.DeleteAsync(_householdId, created.Id, _userId, CancellationToken.None);

        var afterDelete = await _mealPlanService.ListAsync(
            _householdId,
            _userId,
            new DateOnly(2025, 6, 15),
            new DateOnly(2025, 6, 17),
            CancellationToken.None);

        Assert.Empty(afterDelete);
    }

    [Fact]
    public async Task DeleteAsync_FreesMealSlotForReuse()
    {
        var householdRecipe = await _recipeService.CreateAsync(_householdId, _userId, BuildRecipeDto("Repeatable Dinner"), CancellationToken.None);

        var created = await _mealPlanService.CreateAsync(
            _householdId,
            _userId,
            new MealPlanEntryUpsertDto(new DateOnly(2025, 6, 18), MealSlot.Dinner, householdRecipe.Id, 2, null),
            CancellationToken.None);

        await _mealPlanService.DeleteAsync(_householdId, created.Id, _userId, CancellationToken.None);

        var recreated = await _mealPlanService.CreateAsync(
            _householdId,
            _userId,
            new MealPlanEntryUpsertDto(new DateOnly(2025, 6, 18), MealSlot.Dinner, householdRecipe.Id, 3, "Recreated"),
            CancellationToken.None);

        Assert.NotEqual(created.Id, recreated.Id);
        Assert.Equal(MealSlot.Dinner, recreated.MealSlot);
        Assert.Equal(3m, recreated.ServingsPlanned);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SeedBaseData()
    {
        _db.Users.AddRange(
            new User { Id = _userId, Username = "member", DisplayName = "Member" },
            new User { Id = _otherUserId, Username = "other", DisplayName = "Other Member" },
            new User { Id = _outsiderId, Username = "outsider", DisplayName = "Outsider" });

        _db.Households.AddRange(
            new Household { Id = _householdId, Name = "Home", TimeZoneId = "Etc/UTC" },
            new Household { Id = _otherHouseholdId, Name = "Other Home", TimeZoneId = "Etc/UTC" });

        _db.HouseholdMembers.AddRange(
            new HouseholdMember { HouseholdId = _householdId, UserId = _userId, Role = MemberRole.Admin },
            new HouseholdMember { HouseholdId = _otherHouseholdId, UserId = _otherUserId, Role = MemberRole.Admin });

        _db.SaveChanges();
    }

    private static RecipeUpsertDto BuildRecipeDto(string name, int servings = 2)
    {
        return new RecipeUpsertDto(
            name,
            "Reliable family dinner",
            null,
            [
                new RecipeIngredientInputDto("2 cups rice", 2m, "cups", "rice", null),
                new RecipeIngredientInputDto("1 onion, diced", 1m, null, "onion", "diced"),
                new RecipeIngredientInputDto("salt to taste", null, null, null, null)
            ],
            [
                new RecipeInstructionInputDto("Cook the rice.", null),
                new RecipeInstructionInputDto("Saute the onion.", null)
            ],
            servings,
            $"Serves {servings}",
            10,
            20,
            30,
            new RecipeNutritionDto(450, 12, 60, 10),
            "https://cdn.example.com/dinner.jpg",
            ["Dinner", "Family"],
            false);
    }
}