using DoTogether.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DoTogether.Infrastructure.Data.Migrations;

/// <inheritdoc />
[DbContext(typeof(AppDbContext))]
[Migration("20260515120000_TokenOnlyHouseholdInvites")]
public partial class TokenOnlyHouseholdInvites : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "InviteeEmail",
            table: "HouseholdInvites");

        migrationBuilder.Sql("""
            INSERT INTO "HouseholdInvites" (
                "Id",
                "HouseholdId",
                "Token",
                "ExpiresAtUtc",
                "Accepted",
                "CreatedAtUtc",
                "UpdatedAtUtc",
                "IsDeleted"
            )
            SELECT
                h."Id",
                h."Id",
                md5(random()::text || clock_timestamp()::text || h."Id"::text),
                NOW() + INTERVAL '10 years',
                FALSE,
                NOW(),
                NOW(),
                FALSE
            FROM "Households" h
            WHERE h."IsDeleted" = FALSE
              AND NOT EXISTS (
                  SELECT 1
                  FROM "HouseholdInvites" i
                  WHERE i."HouseholdId" = h."Id"
                    AND i."Accepted" = FALSE
                    AND i."IsDeleted" = FALSE
                    AND i."ExpiresAtUtc" > NOW()
              );
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "InviteeEmail",
            table: "HouseholdInvites",
            type: "character varying(256)",
            maxLength: 256,
            nullable: false,
            defaultValue: "");
    }
}