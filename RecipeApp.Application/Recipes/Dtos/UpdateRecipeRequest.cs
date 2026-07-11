using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.Application.Recipes.Dtos;

// Same field list as CreateRecipeRequest, but a separate type: ValidationFilter<T>
// resolves one validator per DTO, and PUT is a full replace — every field is required
// on the wire, none is merged from the stored row.
public record UpdateRecipeRequest(
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
