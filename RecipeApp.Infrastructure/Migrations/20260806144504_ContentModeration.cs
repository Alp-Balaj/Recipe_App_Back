using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipeApp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ContentModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Confidence",
                table: "Reports",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Reports",
                type: "text",
                nullable: false,
                defaultValue: "Human");

            // ── Decision D20: the moderation system user ──────────────────────────────────
            // Hand-written rather than EF HasData, deliberately. HasData would put this row
            // into the model snapshot, which makes it part of every FUTURE migration's diff:
            // any later change to the User entity would re-emit or re-check the seed, and a
            // developer who deleted the row locally would get it silently re-inserted by an
            // unrelated migration. A one-time INSERT is a one-time INSERT.
            //
            // ON CONFLICT DO NOTHING makes it idempotent against a database where it somehow
            // already exists (a re-run, a restored dump), and keeps this from being the
            // migration that fails a deploy over a row it does not own.
            //
            // The PasswordHash is a FORMAT-VALID ASP.NET Identity v3 hash (0x01 marker,
            // HMACSHA512, 100k iterations, 16-byte salt) over random bytes that are not the
            // hash of any password. Format-valid matters: AuthService verifies the password
            // BEFORE it checks IsBanned, and a malformed hash would throw out of
            // VerifyHashedPassword instead of returning false. Random-content matters because
            // no password can then match it. IsBanned is the second, independent lock — a
            // banned account cannot log in even if the first one were ever wrong.
            migrationBuilder.Sql("""
                INSERT INTO "Users" (
                    "Id", "Username", "Email", "PasswordHash", "Bio", "ProfileImageUrl",
                    "CookingRank", "Role", "IsBanned", "TokenVersion",
                    "DefaultRecipeVisibility", "DietaryRestrictions", "CuisinePreferences",
                    "OnboardingCompletedAt", "CreatedAt")
                VALUES (
                    '00000000-0000-0000-0000-00000000ffff',
                    'RecipeApp Moderation',
                    'moderation@recipeapp.invalid',
                    'AQAAAAIAAYagAAAAEH8yzJhG/UaqlxhtCyTEO8TbbcWp8lOOP3P/MtlouyNT09cymN0mgDVj8Uch1JmvOw==',
                    'Automated content moderation. Reports from this account were raised by a classifier and reviewed by a human.',
                    NULL,
                    0, 'User', TRUE, 0,
                    'Public', '[]'::jsonb, '[]'::jsonb,
                    NOW() AT TIME ZONE 'utc',
                    NOW() AT TIME ZONE 'utc')
                ON CONFLICT ("Id") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Every auto-flag references the system user, and Report.ReporterId is Restrict —
            // so those rows have to go before the user can. This is a real down-migration, not
            // a polite one: rolling back the feature discards what the feature produced.
            // Human reports are untouched, which is why the Source filter is here.
            migrationBuilder.Sql("""
                DELETE FROM "Reports" WHERE "Source" = 'Automated';
                DELETE FROM "AiUsageRecords" WHERE "UserId" = '00000000-0000-0000-0000-00000000ffff';
                DELETE FROM "Users" WHERE "Id" = '00000000-0000-0000-0000-00000000ffff';
                """);

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "Reports");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Reports");
        }
    }
}
