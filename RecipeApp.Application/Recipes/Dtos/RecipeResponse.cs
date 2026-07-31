using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.Application.Recipes.Dtos;

public record RecipeResponse(
    Guid Id,
    string Title,
    string Description,
    int PrepTimeMinutes,
    int CookTimeMinutes,
    int TotalTimeMinutes,
    int Servings,
    DifficultyLevel Difficulty,
    string? CuisineType,
    int? CaloriesPerServing,
    string? ImageUrl,
    RecipeVisibility Visibility,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    List<RecipeIngredient> Ingredients,
    List<RecipeStep> Steps,
    List<string> Tags,
    Guid CreatedByUserId,
    // Provenance (stream E, decision D1). Appended, so every existing positional
    // construction site keeps compiling; the SPA badges an AI recipe from the flag.
    // SourceConversationId is null for a recipe generated outside a conversation (and for
    // every recipe authored by hand).
    bool IsAiGenerated,
    Guid? SourceConversationId);
