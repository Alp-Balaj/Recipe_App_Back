using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipeApp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IngredientCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Ingredients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    GramsPerMillilitre = table.Column<double>(type: "double precision", nullable: true),
                    GramsPerPiece = table.Column<double>(type: "double precision", nullable: true),
                    Kcal = table.Column<double>(type: "double precision", nullable: true),
                    ProteinG = table.Column<double>(type: "double precision", nullable: true),
                    FatG = table.Column<double>(type: "double precision", nullable: true),
                    CarbsG = table.Column<double>(type: "double precision", nullable: true),
                    FibreG = table.Column<double>(type: "double precision", nullable: true),
                    FdcId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ingredients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IngredientAliases",
                columns: table => new
                {
                    MatchKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    IngredientId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngredientAliases", x => x.MatchKey);
                    table.ForeignKey(
                        name: "FK_IngredientAliases_Ingredients_IngredientId",
                        column: x => x.IngredientId,
                        principalTable: "Ingredients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IngredientAliases_IngredientId",
                table: "IngredientAliases",
                column: "IngredientId");

            migrationBuilder.CreateIndex(
                name: "IX_Ingredients_Category_Name",
                table: "Ingredients",
                columns: new[] { "Category", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Ingredients_FdcId",
                table: "Ingredients",
                column: "FdcId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ingredients_Name",
                table: "Ingredients",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IngredientAliases");

            migrationBuilder.DropTable(
                name: "Ingredients");
        }
    }
}
