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
            // date_trunc('week', ... AT TIME ZONE 'UTC') returns a timestamp WITHOUT time zone.
            // Assigning that directly to a timestamptz column makes Postgres reinterpret the
            // naive value in the SERVER's local timezone, silently producing a non-UTC,
            // non-midnight instant on any server that isn't already UTC. The trailing
            // `AT TIME ZONE 'UTC'` converts that naive timestamp back into a timestamptz by
            // explicitly telling Postgres the naive value IS UTC, so the stored instant is
            // always UTC midnight regardless of the server's timezone setting.
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
