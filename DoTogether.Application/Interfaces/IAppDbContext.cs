using DoTogether.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DoTogether.Application.Interfaces;

public interface IAppDbContext
{
    DbSet<User> Users { get; }
    DbSet<Household> Households { get; }
    DbSet<HouseholdMember> HouseholdMembers { get; }
    DbSet<HouseholdInvite> HouseholdInvites { get; }
    DbSet<ChoreTemplate> ChoreTemplates { get; }
    DbSet<ChoreOccurrence> ChoreOccurrences { get; }
    DbSet<ChoreEvent> ChoreEvents { get; }
    DbSet<HouseholdRecipe> HouseholdRecipes { get; }
    DbSet<RecipeIngredient> RecipeIngredients { get; }
    DbSet<RecipeInstructionStep> RecipeInstructionSteps { get; }
    DbSet<MealPlanEntry> MealPlanEntries { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<DevicePushToken> DevicePushTokens { get; }
    DbSet<IdempotentOperation> IdempotentOperations { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
