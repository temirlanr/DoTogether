using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoTogether.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAchievementQueryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ChoreOccurrences_HouseholdId_Status_AssigneeId",
                table: "ChoreOccurrences",
                columns: new[] { "HouseholdId", "Status", "AssigneeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChoreOccurrences_HouseholdId_Status_AssigneeId",
                table: "ChoreOccurrences");
        }
    }
}
