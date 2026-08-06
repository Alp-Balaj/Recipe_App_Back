using RecipeApp.Domain.Entities;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.UnitTests;

/// <summary>
/// Stream I. The per-recipe pass lifted out of RecipeInsightService, plus the
/// addition and the coverage rule the plan's day ribbon rests on.
///
/// The tests worth having here are the honest ones: a line that could not be used
/// is uncovered rather than zero, a sum of two unknowns stays unknown, and a day
/// assembled from mostly-unresolvable dishes reports itself as not worth reading.
/// </summary>
public class RecipeNutritionTests
{
    private static readonly Guid FlourId = Guid.NewGuid();
    private static readonly Guid SaltId = Guid.NewGuid();

    private static readonly Dictionary<Guid, Ingredient> Catalogue = new()
    {
        [FlourId] = new Ingredient
        {
            Id = FlourId,
            Name = "Wheat flour",
            Category = "Grains",
            Kcal = 364,
            ProteinG = 10.3,
            FatG = 1,
            CarbsG = 76.3,
            // No fibre figure — USDA publishes calories far more widely than fibre,
            // and the per-nutrient tracking has to survive that.
            FibreG = null,
            GramsPerMillilitre = 0.5708,
        },
        [SaltId] = new Ingredient
        {
            Id = SaltId,
            Name = "Salt",
            Category = "Spices",
            Kcal = 0,
        },
    };

    [Fact]
    public void A_resolved_mass_line_is_summed_and_divided_by_servings()
    {
        // 200 g of flour at 364 kcal/100 g = 728 kcal, over 2 servings = 364 each.
        var totals = RecipeNutrition.PerServing(
            RecipeWith(servings: 2, Line("flour", 200m, UnitOfMeasure.Gram, FlourId)),
            Catalogue);

        Assert.NotNull(totals.Kcal);
        Assert.InRange(totals.Kcal!.Value, 363, 365);
        Assert.Equal(1, totals.CoveredLines);
        Assert.Equal(1, totals.TotalLines);
    }

    [Fact]
    public void Raw_totals_come_back_unrounded_so_a_caller_can_sum_before_rounding()
    {
        // The reason the extraction moved the rounding to the call site: rounding
        // each meal first and adding afterwards makes a day's total disagree with
        // the sum of the meals it is showing.
        var totals = RecipeNutrition.PerServing(
            RecipeWith(servings: 3, Line("flour", 100m, UnitOfMeasure.Gram, FlourId)),
            Catalogue);

        Assert.NotNull(totals.Kcal);
        Assert.NotEqual(Math.Round(totals.Kcal!.Value), totals.Kcal.Value);
    }

    [Fact]
    public void An_unusable_line_is_uncovered_rather_than_zero()
    {
        var totals = RecipeNutrition.PerServing(
            RecipeWith(
                servings: 1,
                Line("flour", 100m, UnitOfMeasure.Gram, FlourId),
                // Resolved, but a pinch has no defensible gram weight.
                Line("salt", 1m, UnitOfMeasure.Pinch, SaltId),
                // Never resolved at all — free text the catalogue does not know.
                Line("gochujang", 2m, UnitOfMeasure.Tablespoon, null)),
            Catalogue);

        Assert.Equal(1, totals.CoveredLines);
        Assert.Equal(3, totals.TotalLines);
    }

    [Fact]
    public void A_nutrient_nothing_published_stays_null_rather_than_becoming_zero()
    {
        var totals = RecipeNutrition.PerServing(
            RecipeWith(servings: 1, Line("flour", 100m, UnitOfMeasure.Gram, FlourId)),
            Catalogue);

        Assert.NotNull(totals.CarbsG);
        // Zero here would read as "this recipe has no fibre", which is a claim the
        // catalogue never made.
        Assert.Null(totals.FibreG);
    }

    [Fact]
    public void A_recipe_with_nothing_resolvable_reports_null_rather_than_zero()
    {
        var totals = RecipeNutrition.PerServing(
            RecipeWith(servings: 2, Line("gochujang", 2m, UnitOfMeasure.Tablespoon, null)),
            Catalogue);

        Assert.Null(totals.Kcal);
        Assert.Equal(0, totals.CoveredLines);
        Assert.Equal(1, totals.TotalLines);
    }

    // ── addition ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Adding_totals_sums_the_figures_and_the_coverage()
    {
        var breakfast = new NutritionTotals(300, 10, 5, 40, 3, 2, 2);
        var dinner = new NutritionTotals(700, 30, 20, 60, 7, 3, 5);

        var day = breakfast.Plus(dinner);

        Assert.Equal(1000, day.Kcal);
        Assert.Equal(40, day.ProteinG);
        Assert.Equal(5, day.CoveredLines);
        Assert.Equal(7, day.TotalLines);
    }

    [Fact]
    public void A_nutrient_known_on_one_side_survives_the_addition()
    {
        // The day still knows its protein even though one meal did not — the
        // coverage line is what says so, not a silently dropped figure.
        var known = new NutritionTotals(300, 10, null, null, null, 2, 2);
        var unknown = new NutritionTotals(null, null, null, null, null, 0, 3);

        var day = known.Plus(unknown);

        Assert.Equal(300, day.Kcal);
        Assert.Equal(10, day.ProteinG);
        Assert.Null(day.FatG);
        Assert.Equal(2, day.CoveredLines);
        Assert.Equal(5, day.TotalLines);
    }

    [Fact]
    public void Two_unknowns_add_up_to_an_unknown_not_a_zero()
    {
        var day = NutritionTotals.Nothing.Plus(new NutritionTotals(null, null, null, null, null, 0, 4));

        Assert.Null(day.Kcal);
        Assert.Equal(0, day.CoveredLines);
        Assert.Equal(4, day.TotalLines);
    }

    // ── D12's trust floor ─────────────────────────────────────────────────────────

    [Fact]
    public void A_well_covered_day_is_worth_rendering_as_a_number()
    {
        Assert.True(new NutritionTotals(1800, 90, 60, 200, 25, 9, 10).IsSufficientlyCovered);
        // Exactly at the floor counts as covered — the rule is "below 80%".
        Assert.True(new NutritionTotals(1800, 90, 60, 200, 25, 8, 10).IsSufficientlyCovered);
    }

    [Fact]
    public void A_thinly_covered_day_is_not()
    {
        // 7 of 10 lines. An undercounted calorie figure is worse than none, so this
        // renders as incomplete rather than as a confident 1,200 (D12).
        Assert.False(new NutritionTotals(1200, 50, 30, 120, 10, 7, 10).IsSufficientlyCovered);
    }

    [Fact]
    public void An_empty_day_is_uncovered_rather_than_perfectly_covered()
    {
        // 0 of 0 is not 100% — dividing nothing by nothing must not read as a
        // complete answer.
        Assert.False(NutritionTotals.Nothing.IsSufficientlyCovered);
        Assert.Equal(0, NutritionTotals.Nothing.Coverage);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static RecipeIngredient Line(string name, decimal quantity, UnitOfMeasure unit, Guid? ingredientId) =>
        new() { Name = name, Quantity = quantity, Unit = unit, IngredientId = ingredientId };

    private static Recipe RecipeWith(int servings, params RecipeIngredient[] ingredients) =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = "Probe",
            Servings = servings,
            Ingredients = [.. ingredients],
        };
}
