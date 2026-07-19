using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipeApp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserDefaultRecipeVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultRecipeVisibility",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "Public");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultRecipeVisibility",
                table: "Users");
        }
    }
}
