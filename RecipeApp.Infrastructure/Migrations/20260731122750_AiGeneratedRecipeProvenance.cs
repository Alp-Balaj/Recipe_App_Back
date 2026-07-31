using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RecipeApp.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AiGeneratedRecipeProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsAiGenerated",
                table: "Recipes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceConversationId",
                table: "Recipes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_SourceConversationId",
                table: "Recipes",
                column: "SourceConversationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Recipes_Conversations_SourceConversationId",
                table: "Recipes",
                column: "SourceConversationId",
                principalTable: "Conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Recipes_Conversations_SourceConversationId",
                table: "Recipes");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_SourceConversationId",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "IsAiGenerated",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "SourceConversationId",
                table: "Recipes");
        }
    }
}
