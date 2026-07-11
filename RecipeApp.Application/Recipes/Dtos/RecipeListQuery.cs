using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Recipes.Dtos;

// Service input for GET /recipes. Wire-level validation is the endpoint's job (cursor
// decoding, difficulty parsing, limit default/clamp) — by the time this record is
// constructed, every field is valid and Limit is the final effective page size.
public record RecipeListQuery(
    string? Cuisine,
    DifficultyLevel? Difficulty,
    IReadOnlyList<string> Tags,
    RecipeListCursor? Cursor,
    int Limit);
