using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipeApp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MealPlanningPromotion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MealPlan_Users_UserId",
                table: "MealPlan");

            migrationBuilder.DropForeignKey(
                name: "FK_MealPlanEntry_MealPlan_MealPlanId",
                table: "MealPlanEntry");

            migrationBuilder.DropForeignKey(
                name: "FK_MealPlanEntry_Recipes_RecipeId",
                table: "MealPlanEntry");

            migrationBuilder.DropForeignKey(
                name: "FK_ShoppingListItem_MealPlan_MealPlanId",
                table: "ShoppingListItem");

            migrationBuilder.DropForeignKey(
                name: "FK_ShoppingListItem_Users_UserId",
                table: "ShoppingListItem");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ShoppingListItem",
                table: "ShoppingListItem");

            migrationBuilder.DropIndex(
                name: "IX_ShoppingListItem_UserId",
                table: "ShoppingListItem");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MealPlanEntry",
                table: "MealPlanEntry");

            migrationBuilder.DropIndex(
                name: "IX_MealPlanEntry_MealPlanId",
                table: "MealPlanEntry");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MealPlan",
                table: "MealPlan");

            migrationBuilder.DropIndex(
                name: "IX_MealPlan_UserId",
                table: "MealPlan");

            migrationBuilder.RenameTable(
                name: "ShoppingListItem",
                newName: "ShoppingListItems");

            migrationBuilder.RenameTable(
                name: "MealPlanEntry",
                newName: "MealPlanEntries");

            migrationBuilder.RenameTable(
                name: "MealPlan",
                newName: "MealPlans");

            migrationBuilder.RenameIndex(
                name: "IX_ShoppingListItem_MealPlanId",
                table: "ShoppingListItems",
                newName: "IX_ShoppingListItems_MealPlanId");

            migrationBuilder.RenameIndex(
                name: "IX_MealPlanEntry_RecipeId",
                table: "MealPlanEntries",
                newName: "IX_MealPlanEntries_RecipeId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ShoppingListItems",
                table: "ShoppingListItems",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MealPlanEntries",
                table: "MealPlanEntries",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MealPlans",
                table: "MealPlans",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingListItems_UserId_CreatedAt_Id",
                table: "ShoppingListItems",
                columns: new[] { "UserId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanEntries_MealPlanId_DayOfWeek_MealType",
                table: "MealPlanEntries",
                columns: new[] { "MealPlanId", "DayOfWeek", "MealType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MealPlans_UserId_WeekStartDate",
                table: "MealPlans",
                columns: new[] { "UserId", "WeekStartDate" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MealPlanEntries_MealPlans_MealPlanId",
                table: "MealPlanEntries",
                column: "MealPlanId",
                principalTable: "MealPlans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MealPlanEntries_Recipes_RecipeId",
                table: "MealPlanEntries",
                column: "RecipeId",
                principalTable: "Recipes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MealPlans_Users_UserId",
                table: "MealPlans",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ShoppingListItems_MealPlans_MealPlanId",
                table: "ShoppingListItems",
                column: "MealPlanId",
                principalTable: "MealPlans",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ShoppingListItems_Users_UserId",
                table: "ShoppingListItems",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MealPlanEntries_MealPlans_MealPlanId",
                table: "MealPlanEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_MealPlanEntries_Recipes_RecipeId",
                table: "MealPlanEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_MealPlans_Users_UserId",
                table: "MealPlans");

            migrationBuilder.DropForeignKey(
                name: "FK_ShoppingListItems_MealPlans_MealPlanId",
                table: "ShoppingListItems");

            migrationBuilder.DropForeignKey(
                name: "FK_ShoppingListItems_Users_UserId",
                table: "ShoppingListItems");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ShoppingListItems",
                table: "ShoppingListItems");

            migrationBuilder.DropIndex(
                name: "IX_ShoppingListItems_UserId_CreatedAt_Id",
                table: "ShoppingListItems");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MealPlans",
                table: "MealPlans");

            migrationBuilder.DropIndex(
                name: "IX_MealPlans_UserId_WeekStartDate",
                table: "MealPlans");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MealPlanEntries",
                table: "MealPlanEntries");

            migrationBuilder.DropIndex(
                name: "IX_MealPlanEntries_MealPlanId_DayOfWeek_MealType",
                table: "MealPlanEntries");

            migrationBuilder.RenameTable(
                name: "ShoppingListItems",
                newName: "ShoppingListItem");

            migrationBuilder.RenameTable(
                name: "MealPlans",
                newName: "MealPlan");

            migrationBuilder.RenameTable(
                name: "MealPlanEntries",
                newName: "MealPlanEntry");

            migrationBuilder.RenameIndex(
                name: "IX_ShoppingListItems_MealPlanId",
                table: "ShoppingListItem",
                newName: "IX_ShoppingListItem_MealPlanId");

            migrationBuilder.RenameIndex(
                name: "IX_MealPlanEntries_RecipeId",
                table: "MealPlanEntry",
                newName: "IX_MealPlanEntry_RecipeId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ShoppingListItem",
                table: "ShoppingListItem",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MealPlan",
                table: "MealPlan",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_MealPlanEntry",
                table: "MealPlanEntry",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingListItem_UserId",
                table: "ShoppingListItem",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MealPlan_UserId",
                table: "MealPlan",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanEntry_MealPlanId",
                table: "MealPlanEntry",
                column: "MealPlanId");

            migrationBuilder.AddForeignKey(
                name: "FK_MealPlan_Users_UserId",
                table: "MealPlan",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MealPlanEntry_MealPlan_MealPlanId",
                table: "MealPlanEntry",
                column: "MealPlanId",
                principalTable: "MealPlan",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MealPlanEntry_Recipes_RecipeId",
                table: "MealPlanEntry",
                column: "RecipeId",
                principalTable: "Recipes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ShoppingListItem_MealPlan_MealPlanId",
                table: "ShoppingListItem",
                column: "MealPlanId",
                principalTable: "MealPlan",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ShoppingListItem_Users_UserId",
                table: "ShoppingListItem",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
