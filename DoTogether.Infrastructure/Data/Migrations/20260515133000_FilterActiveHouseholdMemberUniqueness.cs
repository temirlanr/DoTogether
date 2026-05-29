using DoTogether.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoTogether.Infrastructure.Data.Migrations;

/// <inheritdoc />
[DbContext(typeof(AppDbContext))]
[Migration("20260515133000_FilterActiveHouseholdMemberUniqueness")]
public partial class FilterActiveHouseholdMemberUniqueness : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_HouseholdMembers_HouseholdId_UserId",
            table: "HouseholdMembers");

        migrationBuilder.CreateIndex(
            name: "IX_HouseholdMembers_HouseholdId_UserId",
            table: "HouseholdMembers",
            columns: new[] { "HouseholdId", "UserId" },
            unique: true,
            filter: "\"IsDeleted\" = FALSE");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_HouseholdMembers_HouseholdId_UserId",
            table: "HouseholdMembers");

        migrationBuilder.CreateIndex(
            name: "IX_HouseholdMembers_HouseholdId_UserId",
            table: "HouseholdMembers",
            columns: new[] { "HouseholdId", "UserId" },
            unique: true);
    }
}