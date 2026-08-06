using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.Application.Recipes.Validators;

/// <summary>
/// The step rules stream J added, in one place (the create and update validators mirror each
/// other rule for rule, and three new non-trivial rules written out twice is three chances to
/// drift). The FluentValidation wiring still lives in both validators — that duplication is
/// the file's stated convention and is legible — but the PREDICATES and the bounds are here.
/// </summary>
public static class RecipeStepRules
{
    /// <summary>Matches RecipeGenerationAssistant's own ceiling, so a generated step and a
    /// typed one are bounded identically. A day is already absurd for one step; it exists to
    /// stop an integer from being interesting rather than to judge a slow braise.</summary>
    public const int MaxDurationSeconds = 24 * 60 * 60;

    // Plausible domestic ranges, per unit. The low end is not zero because chilling and
    // freezing steps are real ("rest at -18 °C"); the high end is a hot oven with headroom,
    // not a kiln. A value outside these is far likelier to be a typo or a wrong unit than a
    // recipe, and the two scales need separate bounds because 300 is fine in Fahrenheit and
    // nonsense in Celsius.
    public const int MinCelsius = -40;
    public const int MaxCelsius = 300;
    public const int MinFahrenheit = -40;
    public const int MaxFahrenheit = 575;

    public const string IngredientIndexesMessage =
        "Each step may only reference ingredient lines that exist in this recipe, and may not reference the same line twice.";

    public const string TemperatureMessage =
        "A step temperature must be a plausible cooking temperature for its unit.";

    /// <summary>
    /// Decision D16's server-side half. A step's <see cref="RecipeStep.IngredientIndexes"/>
    /// are positions in the SAME request's ingredient list, so this is the rule that makes
    /// "every stored index is in range" an invariant rather than a hope about clients.
    ///
    /// Distinctness is checked too: a step that lists line 2 twice would render its chip
    /// twice and would double-count the moment anything sums what a step consumes.
    /// </summary>
    public static bool IngredientIndexesAreValid(RecipeStep step, int ingredientCount)
    {
        var indexes = step.IngredientIndexes;
        if (indexes is null || indexes.Count == 0)
        {
            // No references is the common case — "preheat the oven" consumes nothing.
            return true;
        }

        if (indexes.Count > ingredientCount)
        {
            return false;
        }

        var seen = new HashSet<int>(indexes.Count);
        foreach (var index in indexes)
        {
            if (index < 0 || index >= ingredientCount || !seen.Add(index))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Null is valid — most steps have no temperature. A present one must name a unit the
    /// vocabulary knows (the global converter still allows nothing but member names on the
    /// wire, but an undefined enum VALUE binds without complaint, the same hole
    /// CreateRecipeRequestValidator's other IsInEnum rules cover) and sit in that unit's
    /// plausible range.
    /// </summary>
    public static bool TemperatureIsValid(StepTemperature? temperature)
    {
        if (temperature is null)
        {
            return true;
        }

        return temperature.Unit switch
        {
            TemperatureUnit.Celsius => temperature.Value >= MinCelsius && temperature.Value <= MaxCelsius,
            TemperatureUnit.Fahrenheit => temperature.Value >= MinFahrenheit && temperature.Value <= MaxFahrenheit,
            // An undefined enum value reached the DTO — reject rather than guess a scale.
            _ => false,
        };
    }
}
