using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.Application.Recipes.Dtos;

public record CreateRecipeRequest(
    string Title,
    string Description,
    int PrepTimeMinutes,
    int CookTimeMinutes,
    int Servings,
    DifficultyLevel Difficulty,
    string? CuisineType,
    int? CaloriesPerServing,
    string? ImageUrl,
    RecipeVisibility Visibility,
    List<RecipeIngredient> Ingredients,
    List<RecipeStep> Steps,
    List<string> Tags);
