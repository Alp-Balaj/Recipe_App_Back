using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;

namespace RecipeApp.UnitTests;

/// <summary>
/// Stream G, slice G4. The step every computed figure rests on — and the one that
/// must fail honestly, because a fabricated gram weight becomes a fabricated
/// calorie count a person might act on.
/// </summary>
public class NutritionEstimateTests
{
    [Fact]
    public void Mass_converts_with_no_catalogue_help_at_all()
    {
        Assert.Equal(500m, NutritionEstimate.GramsFor(500m, UnitOfMeasure.Gram, null, null));
        Assert.Equal(1000m, NutritionEstimate.GramsFor(1m, UnitOfMeasure.Kilogram, null, null));
    }

    [Fact]
    public void Volume_needs_a_density_and_returns_null_without_one()
    {
        // 2 cups = 480 ml; at flour's 0.5708 g/ml that is 274 g.
        var grams = NutritionEstimate.GramsFor(2m, UnitOfMeasure.Cup, 0.5708, null);
        Assert.NotNull(grams);
        Assert.InRange(grams!.Value, 273m, 275m);

        // Without a density: NULL, not a fallback to water's 1.0. The whole point —
        // guessing would report 480 g of flour, 75% too heavy.
        Assert.Null(NutritionEstimate.GramsFor(2m, UnitOfMeasure.Cup, null, null));
    }

    [Fact]
    public void Count_needs_a_per_piece_weight_and_returns_null_without_one()
    {
        Assert.Equal(150m, NutritionEstimate.GramsFor(3m, UnitOfMeasure.Piece, null, 50));
        Assert.Null(NutritionEstimate.GramsFor(3m, UnitOfMeasure.Piece, null, null));
    }

    [Fact]
    public void Imprecise_units_never_convert_however_much_the_catalogue_knows()
    {
        // A pinch has no defensible gram figure, so no amount of catalogue data makes
        // one available. This is the case where returning SOMETHING would be worst:
        // seasoning appears in nearly every recipe.
        Assert.Null(NutritionEstimate.GramsFor(1m, UnitOfMeasure.Pinch, 1.2, 5));
        Assert.Null(NutritionEstimate.GramsFor(1m, UnitOfMeasure.ToTaste, 1.2, 5));
        Assert.Null(NutritionEstimate.GramsFor(2m, UnitOfMeasure.Handful, 0.5, 30));
    }

    [Fact]
    public void A_non_positive_quantity_contributes_nothing()
    {
        Assert.Null(NutritionEstimate.GramsFor(0m, UnitOfMeasure.Gram, null, null));
        Assert.Null(NutritionEstimate.GramsFor(-5m, UnitOfMeasure.Gram, null, null));
    }
}
