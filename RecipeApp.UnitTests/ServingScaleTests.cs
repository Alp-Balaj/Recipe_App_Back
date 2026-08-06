using RecipeApp.Application.Recipes;
using RecipeApp.Domain.Enums;
using RecipeApp.Domain.ValueObjects;

namespace RecipeApp.UnitTests;

// Decision D17's arithmetic (stream M). Every assertion here is a claim about what serving
// scaling is ALLOWED to touch — the answers matter more than the multiplication, because
// multiplication is the part nothing can get wrong.
public class ServingScaleTests
{
    private static RecipeIngredient Line(string name, decimal quantity, UnitOfMeasure unit) =>
        new() { Name = name, Quantity = quantity, Unit = unit };

    [Theory]
    [InlineData(4, 8, 2.0)]
    [InlineData(4, 2, 0.5)]
    [InlineData(4, 4, 1.0)]
    [InlineData(3, 4, 1.3333333333333333333333333333)]
    public void Factor_is_the_ratio_of_the_two_serving_counts(int from, int to, decimal expected)
    {
        Assert.Equal(expected, ServingScale.Factor(from, to), 10);
    }

    [Fact]
    public void Factor_is_one_when_the_recipe_claims_to_serve_nobody()
    {
        // Dividing by it is worse than not scaling, and a zero-serving recipe is a data
        // problem the cook-mode surface must survive rather than diagnose.
        Assert.Equal(1m, ServingScale.Factor(0, 8));
        Assert.Equal(1m, ServingScale.Factor(-2, 8));
        Assert.Equal(1m, ServingScale.Factor(4, 0));
    }

    [Fact]
    public void Quantities_scale_and_round_to_the_two_decimals_that_are_rendered()
    {
        Assert.Equal(500m, ServingScale.ScaleQuantity(250m, UnitOfMeasure.Gram, 2m));
        Assert.Equal(0.33m, ServingScale.ScaleQuantity(1m, UnitOfMeasure.Cup, 1m / 3m));
    }

    [Fact]
    public void ToTaste_is_the_one_unit_left_alone()
    {
        // Units.Format drops the number entirely for ToTaste, so scaling it is invisible work
        // whose only possible effect is a surprising value in a payload.
        Assert.Equal(1m, ServingScale.ScaleQuantity(1m, UnitOfMeasure.ToTaste, 4m));
    }

    [Fact]
    public void Imprecise_units_other_than_ToTaste_do_scale()
    {
        // A handful of spinach for two is not a handful for eight. Leaving these unscaled
        // would silently under-season a doubled recipe — the number IS rendered for them.
        Assert.Equal(4m, ServingScale.ScaleQuantity(1m, UnitOfMeasure.Handful, 4m));
        Assert.Equal(2m, ServingScale.ScaleQuantity(1m, UnitOfMeasure.Pinch, 2m));
    }

    [Fact]
    public void Count_units_are_allowed_to_come_out_fractional()
    {
        // Halving a recipe genuinely means half an egg. Rounding it up to keep the number
        // tidy would quietly change the recipe, which is a worse answer than an honest 0.5.
        Assert.Equal(0.5m, ServingScale.ScaleQuantity(1m, UnitOfMeasure.Piece, 0.5m));
    }

    [Fact]
    public void ScaleIngredients_returns_copies_and_leaves_the_originals_untouched()
    {
        // Scaling is a VIEW. The entity's lines belong to a tracked row, and scaling in place
        // would let a view concern reach the database through a change tracker that cannot
        // tell the difference.
        var original = new List<RecipeIngredient>
        {
            Line("flour", 250m, UnitOfMeasure.Gram),
            Line("salt", 1m, UnitOfMeasure.ToTaste),
        };

        var scaled = ServingScale.ScaleIngredients(original, 4, 8);

        Assert.Equal(500m, scaled[0].Quantity);
        Assert.Equal(1m, scaled[1].Quantity);
        Assert.Equal(250m, original[0].Quantity);
        Assert.NotSame(original[0], scaled[0]);
    }

    [Fact]
    public void ScaleIngredients_carries_the_catalogue_id_through()
    {
        // Stream G's resolution survives scaling: a doubled line is the same ingredient.
        var id = Guid.NewGuid();
        var original = new List<RecipeIngredient>
        {
            new() { Name = "flour", Quantity = 100m, Unit = UnitOfMeasure.Gram, IngredientId = id },
        };

        Assert.Equal(id, ServingScale.ScaleIngredients(original, 2, 4)[0].IngredientId);
    }
}
