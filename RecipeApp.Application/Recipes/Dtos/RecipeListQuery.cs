using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Recipes.Dtos;

// Service input for GET /recipes and GET /recipes/mine. Wire-level validation is the
// endpoint's job (cursor decoding, difficulty parsing, limit default/clamp) — by the time
// this record is constructed, every field is valid and Limit is the final effective page
// size.
//
// OwnedByUserId is what makes GET /recipes/mine a first-class query rather than a client-
// side filter over the global list: set, the service narrows to that author's rows before
// paging, so the caller's own recipes page properly instead of surfacing only when they
// happen to fall inside a page of everyone's. Optional and last, so every existing
// positional construction keeps compiling as the unfiltered global list.
public record RecipeListQuery(
    string? Cuisine,
    DifficultyLevel? Difficulty,
    IReadOnlyList<string> Tags,
    RecipeListCursor? Cursor,
    int Limit,
    Guid? OwnedByUserId = null);
