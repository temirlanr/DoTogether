using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoTogether.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMealPlanningRecipes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HouseholdRecipes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    OriginType = table.Column<int>(type: "integer", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SourceDomain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SourceAttribution = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SourceRequiresManualReview = table.Column<bool>(type: "boolean", nullable: false),
                    ImportWarningsJson = table.Column<string>(type: "text", nullable: true),
                    Servings = table.Column<int>(type: "integer", nullable: true),
                    YieldText = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PrepMinutes = table.Column<int>(type: "integer", nullable: true),
                    CookMinutes = table.Column<int>(type: "integer", nullable: true),
                    TotalMinutes = table.Column<int>(type: "integer", nullable: true),
                    CaloriesKcal = table.Column<int>(type: "integer", nullable: true),
                    ProteinGrams = table.Column<decimal>(type: "numeric", nullable: true),
                    CarbsGrams = table.Column<decimal>(type: "numeric", nullable: true),
                    FatGrams = table.Column<decimal>(type: "numeric", nullable: true),
                    ImageUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TagsJson = table.Column<string>(type: "text", nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HouseholdRecipes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HouseholdRecipes_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HouseholdRecipes_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HouseholdRecipes_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MealPlanEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    MealSlot = table.Column<int>(type: "integer", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServingsPlanned = table.Column<decimal>(type: "numeric", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MealPlanEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MealPlanEntries_HouseholdRecipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "HouseholdRecipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MealPlanEntries_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MealPlanEntries_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MealPlanEntries_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "RecipeIngredients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdRecipeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    RawText = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric", nullable: true),
                    Unit = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Item = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeIngredients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecipeIngredients_HouseholdRecipes_HouseholdRecipeId",
                        column: x => x.HouseholdRecipeId,
                        principalTable: "HouseholdRecipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeInstructionSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseholdRecipeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Section = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeInstructionSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecipeInstructionSteps_HouseholdRecipes_HouseholdRecipeId",
                        column: x => x.HouseholdRecipeId,
                        principalTable: "HouseholdRecipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdRecipes_CreatedByUserId",
                table: "HouseholdRecipes",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdRecipes_HouseholdId_IsArchived_Name",
                table: "HouseholdRecipes",
                columns: new[] { "HouseholdId", "IsArchived", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdRecipes_UpdatedByUserId",
                table: "HouseholdRecipes",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanEntries_CreatedByUserId",
                table: "MealPlanEntries",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanEntries_HouseholdId_Date_MealSlot",
                table: "MealPlanEntries",
                columns: new[] { "HouseholdId", "Date", "MealSlot" },
                unique: true,
                filter: "\"IsDeleted\" = FALSE");

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanEntries_HouseholdId_RecipeId_Date",
                table: "MealPlanEntries",
                columns: new[] { "HouseholdId", "RecipeId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanEntries_RecipeId",
                table: "MealPlanEntries",
                column: "RecipeId");

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanEntries_UpdatedByUserId",
                table: "MealPlanEntries",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeIngredients_HouseholdRecipeId_SortOrder",
                table: "RecipeIngredients",
                columns: new[] { "HouseholdRecipeId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_RecipeInstructionSteps_HouseholdRecipeId_SortOrder",
                table: "RecipeInstructionSteps",
                columns: new[] { "HouseholdRecipeId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MealPlanEntries");

            migrationBuilder.DropTable(
                name: "RecipeIngredients");

            migrationBuilder.DropTable(
                name: "RecipeInstructionSteps");

            migrationBuilder.DropTable(
                name: "HouseholdRecipes");
        }
    }
}
