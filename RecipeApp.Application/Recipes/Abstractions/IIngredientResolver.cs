using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.Application.Recipes.Abstractions;

/// <summary>
/// Attaches catalogue ids to the ingredient lines of a recipe being written
/// (stream G, slice G2 — decision D8).
///
/// The contract is deliberately narrow, and every part of it is a decision:
///
///   * It MUTATES the lines it is given rather than returning a filtered set, because
///     it must never drop one. An unresolvable ingredient is not an error.
///   * It NEVER touches Name. The name the author typed is what the recipe shows and
///     what the SearchVector indexes; resolution adds an id beside it and nothing more.
///   * It CANNOT fail the write. Any implementation that throws here would turn a
///     catalogue problem into a user's lost recipe.
///
/// Exact-match only, forever. IngredientKey's own doc comment forbids edit distance,
/// trigram similarity and soundex, and its tests pin why (lime/lemon is edit distance 2;
/// butter/butter beans shares most of its trigrams). An LLM canonicaliser may one day
/// supply a better KEY behind that same interface — that is the sanctioned upgrade — but
/// the lookup here stays an equality test.
/// </summary>
public interface IIngredientResolver
{
    /// <summary>
    /// Sets <see cref="RecipeIngredient.IngredientId"/> on every line whose name keys to
    /// a known alias, and leaves the rest null.
    /// </summary>
    Task ResolveAsync(IReadOnlyList<RecipeIngredient> ingredients, CancellationToken cancellationToken = default);
}
