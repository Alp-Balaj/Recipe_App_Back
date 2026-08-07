using RecipeApp.Application.MealPlanning;

namespace RecipeApp.UnitTests;

// The aisle map's job is to turn a NUTRITION taxonomy into a FLOOR PLAN, and the two tests
// that matter are the collapses (several categories, one section) and the fallbacks (a
// category nobody catalogued still has somewhere to go).
public class ShoppingAislesTests
{
    [Theory]
    // Produce is one section, however many categories the catalogue splits it into.
    [InlineData("Vegetables", "Produce")]
    [InlineData("Fruit", "Produce")]
    // So is the butcher's counter.
    [InlineData("Beef", "Meat")]
    [InlineData("Poultry", "Meat")]
    [InlineData("Lamb & game", "Meat")]
    [InlineData("Cured & preserved meat", "Meat")]
    // And the pantry, which is where most of the catalogue's dry goods end up.
    [InlineData("Grains & pasta", "Pantry")]
    [InlineData("Legumes", "Pantry")]
    [InlineData("Fats & oils", "Pantry")]
    [InlineData("Sugar & sweets", "Pantry")]
    // These three keep their own section because a shop does too.
    [InlineData("Dairy & eggs", "Dairy & eggs")]
    [InlineData("Fish & seafood", "Fish & seafood")]
    [InlineData("Baked goods", "Bakery")]
    public void Catalogue_categories_map_to_the_section_a_shop_actually_has(string category, string aisle)
        => Assert.Equal(aisle, ShoppingAisles.ForCategory(category));

    [Fact]
    public void An_unresolved_or_unknown_category_falls_back_to_other()
    {
        // No category at all — the recipe line never resolved to the catalogue.
        Assert.Equal(ShoppingAisles.Other, ShoppingAisles.ForCategory(null));
        // A category a later seed might introduce. It must still shelve somewhere, because
        // the alternative is a group with no heading to render under.
        Assert.Equal(ShoppingAisles.Other, ShoppingAisles.ForCategory("Interstellar produce"));
    }

    [Fact]
    public void Walk_order_leads_with_produce_and_leaves_the_uncatalogued_until_last()
    {
        var walked = new[] { ShoppingAisles.Other, "Drinks", "Pantry", "Produce", "Meat" }
            .OrderBy(ShoppingAisles.RankOf)
            .ToArray();

        Assert.Equal(["Produce", "Meat", "Pantry", "Drinks", ShoppingAisles.Other], walked);
    }

    [Fact]
    public void An_unrecognised_aisle_sorts_last_rather_than_first()
    {
        // Guards the sign of the "not found" rank: Array.IndexOf answers -1, and returning
        // that verbatim would float an unknown aisle to the TOP of the list.
        Assert.True(ShoppingAisles.RankOf("Garden centre") > ShoppingAisles.RankOf(ShoppingAisles.Other));
    }
}
