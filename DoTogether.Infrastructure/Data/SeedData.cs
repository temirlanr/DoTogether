using DoTogether.Application.Interfaces;
using DoTogether.Domain.Entities;
using DoTogether.Domain.Enums;
using DoTogether.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;

namespace DoTogether.Infrastructure.Data;

public static class SeedData
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public static async Task InitializeAsync(IServiceProvider services, bool seedDemoData)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.MigrateAsync();

        if (!seedDemoData)
            return;

        if (await db.Users.IgnoreQueryFilters().AnyAsync())
            return;

        // ── Seed users ──
        var alice = new User
        {
            Id = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            Email = "alice@example.com",
            DisplayName = "Alice",
            PasswordHash = HashPassword("alice")
        };
        var bob = new User 
        { 
            Id = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), 
            Email = "bob@example.com", 
            DisplayName = "Bob",
            PasswordHash = HashPassword("bob")
        };
        db.Users.AddRange(alice, bob);

        // ── Seed household ──
        var household = new Household
        {
            Id = Guid.Parse("cccccccc-0000-0000-0000-000000000003"),
            Name = "Alice & Bob's Home",
            TimeZoneId = "America/New_York"
        };
        db.Households.Add(household);

        // Persist users and household before inserting rows that FK-reference them.
        await db.SaveChangesAsync();

        db.HouseholdMembers.AddRange(
            new HouseholdMember { HouseholdId = household.Id, UserId = alice.Id, Role = MemberRole.Admin },
            new HouseholdMember { HouseholdId = household.Id, UserId = bob.Id, Role = MemberRole.Member });

        // ── Seed chore templates ──
        var dailyDishes = new ChoreTemplate
        {
            Id = Guid.Parse("dddddddd-0000-0000-0000-000000000004"),
            HouseholdId = household.Id,
            Title = "Do the dishes",
            Description = "Wash all dishes in the sink",
            RecurrenceRule = new RecurrenceRule { Type = RecurrenceType.Daily, Interval = 1 },
            AssigneeId = alice.Id,
            CreatedByUserId = alice.Id,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow.Date)
        };

        var weeklyVacuum = new ChoreTemplate
        {
            Id = Guid.Parse("eeeeeeee-0000-0000-0000-000000000005"),
            HouseholdId = household.Id,
            Title = "Vacuum living room",
            RecurrenceRule = new RecurrenceRule { Type = RecurrenceType.Weekly, Interval = 1, DaysOfWeek = [6] }, // Saturday
            AssigneeId = bob.Id,
            CreatedByUserId = alice.Id,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow.Date)
        };

        var monthlyDeepClean = new ChoreTemplate
        {
            Id = Guid.Parse("ffffffff-0000-0000-0000-000000000006"),
            HouseholdId = household.Id,
            Title = "Deep clean bathroom",
            Description = "Monthly deep clean – tests day-31 clamping",
            RecurrenceRule = new RecurrenceRule { Type = RecurrenceType.Monthly, Interval = 1, DayOfMonth = 31 },
            AssigneeId = null,
            CreatedByUserId = bob.Id,
            StartDate = DateOnly.FromDateTime(DateTime.UtcNow.Date)
        };

        db.ChoreTemplates.AddRange(dailyDishes, weeklyVacuum, monthlyDeepClean);
        await db.SaveChangesAsync();

        // Generate occurrences for seed templates.
        var clock = scope.ServiceProvider.GetRequiredService<IDateTimeProvider>();
        var occSvc = scope.ServiceProvider.GetRequiredService<Application.Services.OccurrenceService>();

        foreach (var tpl in new[] { dailyDishes, weeklyVacuum, monthlyDeepClean })
            await occSvc.GenerateAsync(tpl, household.TimeZoneId);

        await db.SaveChangesAsync();
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, Algorithm, HashSize);

        // Store as "iterations.salt.hash" (all base64-encoded)
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }
}
