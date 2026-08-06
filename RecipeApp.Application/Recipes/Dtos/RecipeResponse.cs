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
    Cuisine? CuisineType,
    int? CaloriesPerServing,
    string? ImageUrl,
    RecipeVisibility Visibility,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    List<RecipeIngredient> Ingredients,
    List<RecipeStep> Steps,
    List<RecipeTag> Tags,
    Guid CreatedByUserId,
    // Provenance (stream E, decision D1). Appended, so every existing positional
    // construction site keeps compiling; the SPA badges an AI recipe from the flag.
    // SourceConversationId is null for a recipe generated outside a conversation (and for
    // every recipe authored by hand).
    bool IsAiGenerated,
    Guid? SourceConversationId,
    // Provenance for an imported recipe (stream L, decision D15). Null for every recipe that
    // was typed or generated. Appended, so existing positional construction sites keep
    // compiling.
    //
    // The FULL url, not just the domain, even though the domain is what the detail page shows.
    // The client renders the host and links the whole thing: a reader who wants to check the
    // import against the original needs the page, and a server that returned only the domain
    // would be withholding the one thing that makes D15's auditability claim real.
    //
    // Deliberately absent from UpdateRecipeRequest — that absence IS the immutability
    // mechanism, so anyone adding it there should read Recipe.SourceUrl's comment first.
    string? SourceUrl);
