using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipeApp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CookedDishIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_CookLogs_UserId_RecipeId_CookedAt_Id",
                table: "CookLogs",
                columns: new[] { "UserId", "RecipeId", "CookedAt", "Id" },
                descending: new[] { false, false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_CookedRecipes_UserId_LastCookedAt_RecipeId",
                table: "CookedRecipes",
                columns: new[] { "UserId", "LastCookedAt", "RecipeId" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CookLogs_UserId_RecipeId_CookedAt_Id",
                table: "CookLogs");

            migrationBuilder.DropIndex(
                name: "IX_CookedRecipes_UserId_LastCookedAt_RecipeId",
                table: "CookedRecipes");
        }
    }
}
