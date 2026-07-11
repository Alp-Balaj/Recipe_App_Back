namespace RecipeApp.Application.Recipes.Dtos;

public record RecipeListResponse(
    IReadOnlyList<RecipeResponse> Items,
    string? NextCursor);
