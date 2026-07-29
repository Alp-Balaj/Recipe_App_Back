using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipeApp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ShoppingListProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "WeekStartDate",
                table: "ShoppingListItems",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateTable(
                name: "ShoppingListMarks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WeekStartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsPurchased = table.Column<bool>(type: "boolean", nullable: false),
                    IsSuppressed = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShoppingListMarks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShoppingListMarks_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingListItems_UserId_WeekStartDate",
                table: "ShoppingListItems",
                columns: new[] { "UserId", "WeekStartDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingListMarks_UserId_WeekStartDate_Key",
                table: "ShoppingListMarks",
                columns: new[] { "UserId", "WeekStartDate", "Key" },
                unique: true);

            // Generated rows are regenerable by definition — the projection derives them
            // from the plan on every read. Purchased ticks on them are lost once here, which
            // is accepted: the old generate endpoint already destroyed them on every run.
            migrationBuilder.Sql(@"DELETE FROM ""ShoppingListItems"" WHERE ""MealPlanId"" IS NOT NULL;");

            // Manual rows keep their identity and gain a week: the Monday of CreatedAt, in UTC.
            // date_trunc('week') is Monday-based in Postgres, which matches WeekStartDate.
            //
            // DO NOT "simplify" this to a single `date_trunc('week', "CreatedAt" AT TIME ZONE
            // 'UTC')` assigned straight into WeekStartDate — that form is silently wrong on any
            // server whose TimeZone setting isn't UTC. `date_trunc('week', ts AT TIME ZONE
            // 'UTC')` returns a timestamp WITHOUT time zone (a naive value already in UTC).
            // Assigning that naive value directly into a `timestamptz` column does NOT store it
            // as UTC: Postgres reinterprets the naive value as being in the SERVER's local
            // TimeZone setting and converts FROM there, shifting the stored instant by the
            // server's UTC offset. Measured on a Europe/Belgrade (UTC+2) server with
            // "CreatedAt" = 2026-07-29 14:30:00+00 (a Wednesday): the single-conversion form
            // stored 2026-07-26 22:00:00 UTC — Sunday night, not Monday — while the form below
            // (with the second, explicit `AT TIME ZONE 'UTC'`) stored 2026-07-27 00:00:00 UTC,
            // the correct Monday midnight. The second `AT TIME ZONE 'UTC'` is what reinterprets
            // the naive date_trunc result as UTC (rather than server-local) before Postgres
            // converts it into the timestamptz column; removing it reintroduces the bug on any
            // non-UTC server, including this one.
            migrationBuilder.Sql(@"
                UPDATE ""ShoppingListItems""
                SET ""WeekStartDate"" = (date_trunc('week', ""CreatedAt"" AT TIME ZONE 'UTC')) AT TIME ZONE 'UTC'
                WHERE ""MealPlanId"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShoppingListMarks");

            migrationBuilder.DropIndex(
                name: "IX_ShoppingListItems_UserId_WeekStartDate",
                table: "ShoppingListItems");

            migrationBuilder.DropColumn(
                name: "WeekStartDate",
                table: "ShoppingListItems");
        }
    }
}
