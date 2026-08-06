using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.Application.Recipes.Import;

/// <summary>
/// One recipe read off a source, before it becomes a row (stream L).
///
/// BOTH import paths produce this same type, and that is the point of it: the deterministic
/// JSON-LD parse and the model extraction disagree about almost everything — one is free and
/// certain, the other costs money and guesses — but they agree completely on what a recipe
/// IS. Everything downstream of here (normalisation, validation, ingredient resolution,
/// persistence, moderation) is written once and cannot drift between the two tiers, which is
/// what stops the LLM path from quietly acquiring different rules than the free one.
///
/// Deliberately NOT a <c>CreateRecipeRequest</c>, for two reasons that both bite later:
///
///   1. Every field here is OPTIONAL in a way the request's is not. A real recipe page may
///      carry no description and no servings; the request may not. Normalisation is the step
///      between, and giving the draft the request's non-null shape would force the parser to
///      invent values at the point where it still knows whether it found them.
///   2. <see cref="ImageUrl"/> means something different here — see below.
///
/// Also deliberately NOT a Recipe entity: ownership, visibility and provenance are the
/// orchestrator's to decide and never the source's. A page that helpfully declared itself
/// public would otherwise be asserting something about a user's account.
/// </summary>
public sealed record ImportedRecipeDraft
{
    public string? Title { get; init; }

    public string? Description { get; init; }

    public int? PrepTimeMinutes { get; init; }

    public int? CookTimeMinutes { get; init; }

    public int? Servings { get; init; }

    /// <summary>Null where the source said nothing — the normaliser picks the default, not the parser.</summary>
    public DifficultyLevel? Difficulty { get; init; }

    public Cuisine? CuisineType { get; init; }

    public int? CaloriesPerServing { get; init; }

    public List<RecipeIngredient> Ingredients { get; init; } = [];

    /// <summary>
    /// Steps in source order. <see cref="RecipeStep.StepNumber"/> is NOT trusted from here —
    /// the normaliser renumbers from position, exactly as the generator does, because a source
    /// that numbers its own steps and a source that does not must produce the same row.
    /// </summary>
    public List<RecipeStep> Steps { get; init; } = [];

    public List<RecipeTag> Tags { get; init; } = [];

    /// <summary>
    /// The image URL AS THE SOURCE PUBLISHED IT — a remote, foreign address, which is the one
    /// thing <c>Recipe.ImageUrl</c> must never end up holding. It is re-hosted through
    /// <c>IImageStorage</c> before it reaches the entity, so the column keeps meaning "an
    /// object we store" everywhere in the app. Naming these two fields the same thing is the
    /// trap this comment exists to mark.
    /// </summary>
    public string? ImageUrl { get; init; }
}
