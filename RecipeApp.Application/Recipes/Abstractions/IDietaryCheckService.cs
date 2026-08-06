using RecipeApp.Application.Recipes.Dtos;
using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;

namespace RecipeApp.Application.Recipes.Abstractions;

/// <summary>
/// Runs <see cref="DietaryCheck"/> over a BATCH of recipes with one catalogue read
/// (stream H).
///
/// The batch shape is the whole point. A week proposal is up to 21 slots and the
/// obvious per-slot call would be 21 catalogue queries for one button press — the
/// same N-reads-per-surface mistake the month view refused twice. Callers hand over
/// the recipes they already hold (candidate recipes in the proposal lane, the
/// freshly written row in the generator lane), so nothing here re-reads a recipe
/// either.
///
/// Restrictions are a PARAMETER, not a lookup, and deliberately: both AI lanes have
/// already loaded the caller's own row before they reach this, and passing them in
/// keeps "checked against the CALLER's restrictions" a visible fact at the call site
/// rather than a hidden query. It also means this service never has an opinion about
/// whose restrictions apply — which is the one thing that would be dangerous to get
/// wrong.
/// </summary>
public interface IDietaryCheckService
{
    /// <summary>
    /// Verdicts keyed by recipe id. Every recipe passed in gets an entry; a recipe
    /// with nothing to report gets an empty list, and so does every recipe when
    /// <paramref name="restrictions"/> is empty (in which case no query runs at all).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<DietaryCheckResponse>>> CheckAsync(
        IReadOnlyCollection<Recipe> recipes,
        IReadOnlyCollection<DietaryRestriction> restrictions,
        CancellationToken cancellationToken = default);
}
