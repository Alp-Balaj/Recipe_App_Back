using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.Application.Recipes;

/// <summary>
/// Serving scaling, as arithmetic (stream M, decision D17).
///
/// ── D17, SETTLED ──────────────────────────────────────────────────────────────────────────
/// Stream J settled the rendering half: a step's quantity is rendered FROM the referenced
/// ingredient line and never repeated in the prose. This settles the rest of it.
///
/// <b>Scaling happens, it is multiplication, and it touches ingredient QUANTITIES and nothing
/// else.</b> Not the step prose, not temperatures, not durations, and never the model.
///
///   • <b>Prose is left verbatim.</b> Rewriting "add 200 g of flour" to say 400 needs a model,
///     and a model that rewrites a method is a model that can quietly change the method. J's
///     rendering rule is what makes leaving the prose alone survivable: the authoritative
///     number is the one on the ingredient chip, which is computed here. The surface still has
///     to be honest that the two can disagree in an older recipe — the cook-mode banner says
///     the written instructions are the author's own words, and that is a UI obligation this
///     class cannot discharge for it.
///   • <b>Temperature never scales.</b> 180 °C for eight people is 180 °C. A pan gets bigger,
///     an oven does not get hotter.
///   • <b>Duration never scales.</b> Doubling a braise does not double the braise; it barely
///     moves it, and the direction is not even reliably up. An honest "unchanged" beats a
///     confident wrong number.
///
/// Scaling is a VIEW. Nothing here writes: the recipe's stored servings is the author's
/// statement about the dish, and a reader cooking for eight has not edited it.
/// </summary>
public static class ServingScale
{
    /// <summary>Widest scale the surfaces offer. A factor is only ever derived from two
    /// serving counts, both of which the write paths already bound, so this is a guard on
    /// arithmetic rather than a product decision.</summary>
    public const int MinServings = 1;
    public const int MaxServings = 100;

    /// <summary>
    /// The factor that takes <paramref name="fromServings"/> to <paramref name="toServings"/>.
    /// A non-positive source is 1m — a recipe that claims to serve nobody cannot be scaled, and
    /// dividing by it is worse than not scaling.
    /// </summary>
    public static decimal Factor(int fromServings, int toServings)
    {
        if (fromServings <= 0 || toServings <= 0)
        {
            return 1m;
        }

        return (decimal)toServings / fromServings;
    }

    /// <summary>
    /// One quantity, scaled and rounded to the two decimals <c>Units.Format</c> renders.
    ///
    /// <see cref="UnitOfMeasure.ToTaste"/> is the ONE unit left alone, and the reason is that
    /// its quantity is never rendered — <c>Units.Format</c> drops the number entirely, so
    /// scaling it is invisible work whose only possible effect is a surprising value in a
    /// serialized payload. Every other unit's number is read and acted on by a cook, pinches
    /// and handfuls included: a handful of spinach for two is not a handful for eight, and
    /// leaving "imprecise" units unscaled would silently under-season a doubled recipe.
    /// </summary>
    public static decimal ScaleQuantity(decimal quantity, UnitOfMeasure unit, decimal factor)
    {
        if (unit == UnitOfMeasure.ToTaste || factor == 1m)
        {
            return quantity;
        }

        return Math.Round(quantity * factor, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// A copy of the ingredient list at the target serving count. A COPY on purpose: the
    /// entity's lines belong to the tracked row, and scaling them in place would let a view
    /// concern reach the database through a change tracker that cannot tell the difference.
    /// </summary>
    public static List<RecipeIngredient> ScaleIngredients(
        IReadOnlyList<RecipeIngredient> ingredients,
        int fromServings,
        int toServings)
    {
        var factor = Factor(fromServings, toServings);
        return ingredients
            .Select(i => new RecipeIngredient
            {
                Name = i.Name,
                Quantity = ScaleQuantity(i.Quantity, i.Unit, factor),
                Unit = i.Unit,
                IngredientId = i.IngredientId,
            })
            .ToList();
    }
}
