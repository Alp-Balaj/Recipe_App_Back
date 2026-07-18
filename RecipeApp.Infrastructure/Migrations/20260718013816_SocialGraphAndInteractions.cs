using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipeApp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SocialGraphAndInteractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserFollow_Users_FollowerId",
                table: "UserFollow");

            migrationBuilder.DropForeignKey(
                name: "FK_UserFollow_Users_FollowingId",
                table: "UserFollow");

            migrationBuilder.DropIndex(
                name: "IX_Comments_RecipeId",
                table: "Comments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_UserFollow",
                table: "UserFollow");

            migrationBuilder.DropIndex(
                name: "IX_UserFollow_FollowingId",
                table: "UserFollow");

            migrationBuilder.RenameTable(
                name: "UserFollow",
                newName: "UserFollows");

            migrationBuilder.AddPrimaryKey(
                name: "PK_UserFollows",
                table: "UserFollows",
                columns: new[] { "FollowerId", "FollowingId" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedRecipes_UserId_SavedAt_RecipeId",
                table: "SavedRecipes",
                columns: new[] { "UserId", "SavedAt", "RecipeId" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_RecipeId_CreatedAt_Id",
                table: "Comments",
                columns: new[] { "RecipeId", "CreatedAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_UserFollows_FollowerId_FollowedAt_FollowingId",
                table: "UserFollows",
                columns: new[] { "FollowerId", "FollowedAt", "FollowingId" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_UserFollows_FollowingId_FollowedAt_FollowerId",
                table: "UserFollows",
                columns: new[] { "FollowingId", "FollowedAt", "FollowerId" },
                descending: new[] { false, true, true });

            migrationBuilder.AddForeignKey(
                name: "FK_UserFollows_Users_FollowerId",
                table: "UserFollows",
                column: "FollowerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_UserFollows_Users_FollowingId",
                table: "UserFollows",
                column: "FollowingId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserFollows_Users_FollowerId",
                table: "UserFollows");

            migrationBuilder.DropForeignKey(
                name: "FK_UserFollows_Users_FollowingId",
                table: "UserFollows");

            migrationBuilder.DropIndex(
                name: "IX_SavedRecipes_UserId_SavedAt_RecipeId",
                table: "SavedRecipes");

            migrationBuilder.DropIndex(
                name: "IX_Comments_RecipeId_CreatedAt_Id",
                table: "Comments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_UserFollows",
                table: "UserFollows");

            migrationBuilder.DropIndex(
                name: "IX_UserFollows_FollowerId_FollowedAt_FollowingId",
                table: "UserFollows");

            migrationBuilder.DropIndex(
                name: "IX_UserFollows_FollowingId_FollowedAt_FollowerId",
                table: "UserFollows");

            migrationBuilder.RenameTable(
                name: "UserFollows",
                newName: "UserFollow");

            migrationBuilder.AddPrimaryKey(
                name: "PK_UserFollow",
                table: "UserFollow",
                columns: new[] { "FollowerId", "FollowingId" });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_RecipeId",
                table: "Comments",
                column: "RecipeId");

            migrationBuilder.CreateIndex(
                name: "IX_UserFollow_FollowingId",
                table: "UserFollow",
                column: "FollowingId");

            migrationBuilder.AddForeignKey(
                name: "FK_UserFollow_Users_FollowerId",
                table: "UserFollow",
                column: "FollowerId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_UserFollow_Users_FollowingId",
                table: "UserFollow",
                column: "FollowingId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
