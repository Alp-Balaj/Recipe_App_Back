using RecipeApp.Domain.Enums;
using RecipeApp.Domain.Services;

namespace RecipeApp.UnitTests;

/// <summary>
/// Stream G, slice G4. These pin the rules' SHAPE more than their contents: the
/// keyword list will grow, but word-boundary matching and "conflicts, never
/// compliance" must not change.
/// </summary>
public class DietaryRulesTests
{
    private static IReadOnlyList<(string, string)> Check(
        DietaryRestriction restriction, params (string Name, string Category)[] ingredients) =>
        DietaryRules.Conflicts(restriction, ingredients);

    [Fact]
    public void A_meat_category_conflicts_with_vegetarian()
    {
        // Category matching is stronger evidence than any keyword: "this row is a
        // beef product" is a fact about the catalogue entry, not a guess at its name.
        var conflicts = Check(DietaryRestriction.Vegetarian, ("Chuck for stew beef", "Beef"));
        var conflict = Assert.Single(conflicts);
        Assert.Contains("Beef", conflict.Item2);
    }

    [Fact]
    public void Pescatarian_permits_fish_but_not_meat()
    {
        Assert.Empty(Check(DietaryRestriction.Pescatarian, ("Atlantic cod", "Fish & seafood")));
        Assert.Single(Check(DietaryRestriction.Pescatarian, ("Bacon pork", "Pork")));
    }

    [Fact]
    public void Vegan_excludes_dairy_where_vegetarian_does_not()
    {
        Assert.Empty(Check(DietaryRestriction.Vegetarian, ("Cheddar cheese", "Dairy & eggs")));
        Assert.Single(Check(DietaryRestriction.Vegan, ("Cheddar cheese", "Dairy & eggs")));
    }

    [Theory]
    [InlineData("Egg whole", true)]
    [InlineData("Eggs", true)]          // a trailing s is the same word
    [InlineData("Eggplant", false)]     // ...but "eggplant" is not
    [InlineData("Egg noodles", true)]
    public void Keywords_match_on_word_boundaries(string name, bool expectConflict)
    {
        // The rule that keeps a 17-keyword list from producing confident nonsense. A
        // substring test would fire EggFree on every aubergine in the catalogue.
        var conflicts = Check(DietaryRestriction.EggFree, (name, "Vegetables"));
        Assert.Equal(expectConflict, conflicts.Count > 0);
    }

    [Fact]
    public void Nut_free_and_peanut_free_are_separate_rules()
    {
        // A peanut is a legume, and someone allergic to tree nuts is often not
        // allergic to peanuts. Collapsing the two would be medically wrong.
        Assert.Empty(Check(DietaryRestriction.NutFree, ("Peanut butter", "Legumes")));
        Assert.Single(Check(DietaryRestriction.PeanutFree, ("Peanut butter", "Legumes")));
        Assert.Single(Check(DietaryRestriction.NutFree, ("Almond flour", "Nuts & seeds")));
    }

    [Fact]
    public void An_unlisted_restriction_reports_nothing_rather_than_guessing()
    {
        // LowCarb and LowSodium are thresholds on a TOTAL, not properties of any one
        // ingredient, so no keyword rule exists for them and none should be invented.
        Assert.Empty(Check(DietaryRestriction.LowCarb, ("Granulated sugars", "Sugar & sweets")));
        Assert.Empty(Check(DietaryRestriction.LowSodium, ("Table salt", "Spices & herbs")));
    }

    [Fact]
    public void The_reason_names_what_was_found()
    {
        // A conflict a user cannot understand is a conflict they will ignore.
        var conflict = Assert.Single(Check(DietaryRestriction.GlutenFree, ("White wheat flour", "Grains & pasta")));
        Assert.Equal("White wheat flour", conflict.Item1);
        Assert.Equal("contains wheat", conflict.Item2);
    }
}
